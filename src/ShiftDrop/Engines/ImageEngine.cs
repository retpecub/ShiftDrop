using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ImageMagick;
using ShiftDrop.Core;
using ShiftDrop.UI;

namespace ShiftDrop.Engines;

/// <summary>Картинки через Magick.NET (ImageMagick): JPG/PNG/WEBP/HEIC/AVIF/ICO/TIFF/BMP/GIF/PSD/TGA.</summary>
public static class ImageEngine
{
    private static MagickFormat FormatOf(string ext) => ext switch
    {
        "jpg" or "jpeg" or "jfif" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "tif" or "tiff" => MagickFormat.Tiff,
        "ico" => MagickFormat.Ico,
        "avif" => MagickFormat.Avif,
        "heic" or "heif" => MagickFormat.Heic,
        _ => throw new NotSupportedException("Формат не поддерживается: " + ext),
    };

    private static MagickImage Load(string path)
    {
        var img = new MagickImage(path);
        img.AutoOrient(); // учесть поворот из EXIF до любых операций
        return img;
    }

    private static void FlattenWhite(MagickImage img)
    {
        if (!img.HasAlpha) return;
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaOption.Remove);
    }

    private static bool IsAnimated(string path)
    {
        var ext = PathUtil.Ext(path);
        if (ext is not ("gif" or "webp")) return false;
        try
        {
            var info = MagickImageInfo.ReadCollection(path);
            return info.Count() > 1;
        }
        catch
        {
            return false;
        }
    }

    public static void Convert(JobContext ctx, string src, string target)
    {
        var dst = PathUtil.Output(src, target);

        if (target == "pdf")
        {
            var (bytes, w, h) = ToJpeg(src, 92);
            File.WriteAllBytes(dst, PdfEngine.JpegsToPdf(new[] { (bytes, w, h) }));
            ctx.AddOutput(dst);
            return;
        }

        if (target is "gif" or "webp" && IsAnimated(src))
        {
            using var frames = new MagickImageCollection(src);
            frames.Coalesce();
            frames.Write(dst, FormatOf(target));
            ctx.AddOutput(dst);
            return;
        }

        using var img = Load(src);
        switch (target)
        {
            case "jpg":
                FlattenWhite(img);
                img.Quality = 90;
                break;
            case "bmp":
                FlattenWhite(img);
                break;
            case "webp":
            case "avif":
                img.Quality = 85;
                break;
            case "ico":
                if (img.Width > 256 || img.Height > 256)
                    img.Resize(256, 256); // с сохранением пропорций
                break;
        }
        img.Write(dst, FormatOf(target));
        ctx.AddOutput(dst);
    }

    /// <summary>Картинка в JPEG-байты (для сборки PDF).</summary>
    public static (byte[] bytes, int width, int height) ToJpeg(string src, int quality)
    {
        using var img = Load(src);
        FlattenWhite(img);
        img.Strip();
        img.Quality = quality;
        img.ColorSpace = ColorSpace.sRGB;
        return (img.ToByteArray(MagickFormat.Jpeg), img.Width, img.Height);
    }

    public static void Compress(JobContext ctx, string src)
    {
        var ext = PathUtil.Ext(src);
        using var img = Load(src);
        img.Strip();

        string outExt;
        switch (ext)
        {
            case "png":
                outExt = "png";
                img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                img.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
                break;
            case "webp":
                outExt = "webp";
                img.Quality = 72;
                break;
            case "avif":
                outExt = "avif";
                img.Quality = 60;
                break;
            case "gif":
            case "ico":
                outExt = ext;
                break;
            default:
                // jpg, а также bmp/tiff/heic/psd — сжимаем в JPEG
                outExt = "jpg";
                FlattenWhite(img);
                img.Quality = 75;
                break;
        }

        // Очень большие фото ужимаем до 4096 px по длинной стороне
        if (Math.Max(img.Width, img.Height) > 4096)
            img.Resize(new MagickGeometry(4096, 4096));

        var dst = PathUtil.Output(src, outExt, "_compressed");
        img.Write(dst, FormatOf(outExt));
        ctx.AddOutput(dst);
    }

    public static void StripMetadata(JobContext ctx, string src)
    {
        var ext = PathUtil.Ext(src);
        using var img = Load(src);
        img.Strip();
        if (ext is "jpg" or "jpeg" or "jfif") img.Quality = 95;
        var outExt = ext switch
        {
            "jpeg" or "jfif" or "heic" or "heif" => "jpg", // HEIC сохраняем в JPG: энкодера HEIC может не быть
            "tif" => "tiff",
            "psd" or "tga" => "png",
            _ => ext,
        };
        var dst = PathUtil.Output(src, outExt, "_clean");
        img.Write(dst, FormatOf(outExt));
        ctx.AddOutput(dst);
    }

    public static void Resize(JobContext ctx, string src, int percent)
    {
        using var img = Load(src);
        img.Resize(new Percentage(percent));
        WriteSameFormat(ctx, img, src, $"_{percent}");
    }

    public static void Rotate(JobContext ctx, string src, int degrees)
    {
        using var img = Load(src);
        img.Rotate(degrees);
        WriteSameFormat(ctx, img, src, "_rotated");
    }

    public static void Crop(JobContext ctx, string src)
    {
        using var img = Load(src);

        // Превью для окна обрезки — не больше 1200×800
        const int maxW = 1200, maxH = 800;
        double k = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
        int pw = Math.Max(1, (int)(img.Width * k)), ph = Math.Max(1, (int)(img.Height * k));
        byte[] preview;
        using (var p = (MagickImage)img.Clone())
        {
            p.Resize(pw, ph);
            pw = p.Width;
            ph = p.Height;
            preview = p.ToByteArray(MagickFormat.Png);
        }

        int ow = img.Width, oh = img.Height;
        var rect = Application.Current.Dispatcher.Invoke(() =>
        {
            var w = new CropWindow(preview, pw, ph, ow, oh, Path.GetFileName(src));
            return w.ShowDialog() == true ? w.Result : null;
        });
        if (rect is null) return; // пользователь отменил

        var r = rect.Value;
        img.Crop(new MagickGeometry(r.X, r.Y, r.Width, r.Height));
        img.RePage();
        WriteSameFormat(ctx, img, src, "_cropped");
    }

    public static void CombineToPdf(JobContext ctx, IReadOnlyList<string> files)
    {
        var pages = new List<(byte[] bytes, int w, int h)>();
        for (int i = 0; i < files.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Status($"{i + 1} из {files.Count}: {Path.GetFileName(files[i])}");
            ctx.Progress((double)i / files.Count);
            pages.Add(ToJpeg(files[i], 90));
        }
        ctx.Status("Собираю PDF…");
        var dst = PathUtil.Output(files[0], "pdf", files.Count > 1 ? "_combined" : "");
        File.WriteAllBytes(dst, PdfEngine.JpegsToPdf(pages));
        ctx.AddOutput(dst);
    }

    private static void WriteSameFormat(JobContext ctx, MagickImage img, string src, string suffix)
    {
        var ext = PathUtil.Ext(src);
        var outExt = ext switch
        {
            "jpeg" or "jfif" => "jpg",
            "heic" or "heif" => "jpg",
            "tif" => "tiff",
            "psd" or "tga" => "png",
            _ => ext,
        };
        if (outExt == "jpg") img.Quality = 92;
        var dst = PathUtil.Output(src, outExt, suffix);
        img.Write(dst, FormatOf(outExt));
        ctx.AddOutput(dst);
    }
}
