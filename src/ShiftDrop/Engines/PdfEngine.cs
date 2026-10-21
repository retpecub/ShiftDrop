using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Docnet.Core;
using Docnet.Core.Editors;
using Docnet.Core.Models;
using ImageMagick;
using ShiftDrop.Core;

namespace ShiftDrop.Engines;

/// <summary>PDF через Docnet (PDFium): страницы в картинки, склейка, разбиение, JPEG → PDF.</summary>
public static class PdfEngine
{
    private const double RenderDpi = 150;

    // PDFium не потокобезопасен — все вызовы под одной блокировкой
    private static readonly object PdfiumLock = new();

    public static void ToImages(JobContext ctx, string src, string target)
    {
        lock (PdfiumLock)
        {
            using var doc = DocLib.Instance.GetDocReader(src, new PageDimensions(RenderDpi / 72.0));
            int count = doc.GetPageCount();
            var pad = new string('0', count.ToString().Length);

            for (int i = 0; i < count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress((double)i / count);
                if (count > 1) ctx.Status($"{Path.GetFileName(src)} — стр. {i + 1} из {count}");

                using var page = doc.GetPageReader(i);
                int w = page.GetPageWidth();
                int h = page.GetPageHeight();
                byte[] bgra = page.GetImage();

                FlattenOnWhite(bgra);
                using var img = new MagickImage(BuildBmp(bgra, w, h));
                img.Density = new Density(RenderDpi, RenderDpi, DensityUnit.PixelsPerInch);

                var suffix = count == 1 ? "" : "_p" + (i + 1).ToString(pad);
                var dst = PathUtil.Output(src, target, suffix);
                if (target == "jpg") img.Quality = 90;
                img.Write(dst, target == "png" ? MagickFormat.Png : MagickFormat.Jpeg);
                ctx.AddOutput(dst);
            }
        }
    }

    public static void Merge(JobContext ctx, IReadOnlyList<string> files)
    {
        if (files.Count < 2)
            throw new InvalidOperationException("Для склейки выделите два или больше PDF");

        lock (PdfiumLock)
        {
            byte[] acc = File.ReadAllBytes(files[0]);
            for (int i = 1; i < files.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Status($"Добавляю {i + 1} из {files.Count}: {Path.GetFileName(files[i])}");
                ctx.Progress((double)i / files.Count);
                acc = DocLib.Instance.Merge(acc, File.ReadAllBytes(files[i]));
            }
            var dst = PathUtil.Output(files[0], "pdf", "_merged");
            File.WriteAllBytes(dst, acc);
            ctx.AddOutput(dst);
        }
    }

    public static void Split(JobContext ctx, string src)
    {
        lock (PdfiumLock)
        {
            int count;
            using (var doc = DocLib.Instance.GetDocReader(src, new PageDimensions(1.0)))
                count = doc.GetPageCount();

            if (count < 2)
                throw new InvalidOperationException("В документе одна страница");

            var pad = new string('0', count.ToString().Length);
            for (int i = 0; i < count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress((double)i / count);
                var bytes = DocLib.Instance.Split(src, i, i);
                var dst = PathUtil.Output(src, "pdf", "_p" + (i + 1).ToString(pad));
                File.WriteAllBytes(dst, bytes);
                ctx.AddOutput(dst);
            }
        }
    }

    /// <summary>
    /// JPEG-картинки → PDF, по странице на картинку. Размер страницы — длинная сторона 842 pt (как A4),
    /// при этом картинка вставляется в исходном разрешении.
    /// </summary>
    public static byte[] JpegsToPdf(IEnumerable<(byte[] bytes, int w, int h)> images)
    {
        var list = images.Select(i =>
        {
            double k = 842.0 / Math.Max(i.w, i.h);
            return new JpegImage
            {
                Bytes = i.bytes,
                Width = Math.Max(1, (int)Math.Round(i.w * k)),
                Height = Math.Max(1, (int)Math.Round(i.h * k)),
            };
        }).ToList();

        lock (PdfiumLock)
            return DocLib.Instance.JpegToPdf(list);
    }

    /// <summary>PDFium отдаёт BGRA с прозрачным фоном — накладываем на белый.</summary>
    private static void FlattenOnWhite(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            int inv = 255 - a;
            bgra[i] = (byte)((bgra[i] * a + 255 * inv) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 255 * inv) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 255 * inv) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>Сырые BGRA-пиксели → 32-битный BMP (top-down), который читает ImageMagick.</summary>
    private static byte[] BuildBmp(byte[] bgra, int w, int h)
    {
        const int headerSize = 14 + 40;
        int dataSize = w * h * 4;
        var bmp = new byte[headerSize + dataSize];
        using (var ms = new MemoryStream(bmp))
        using (var bw = new BinaryWriter(ms))
        {
            // BITMAPFILEHEADER
            bw.Write((byte)'B');
            bw.Write((byte)'M');
            bw.Write(headerSize + dataSize);
            bw.Write(0);
            bw.Write(headerSize);
            // BITMAPINFOHEADER
            bw.Write(40);
            bw.Write(w);
            bw.Write(-h); // отрицательная высота = строки сверху вниз
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0); // BI_RGB
            bw.Write(dataSize);
            bw.Write(5906); // ~150 dpi в пикселях на метр
            bw.Write(5906);
            bw.Write(0);
            bw.Write(0);
        }
        Buffer.BlockCopy(bgra, 0, bmp, headerSize, Math.Min(dataSize, bgra.Length));
        return bmp;
    }
}
