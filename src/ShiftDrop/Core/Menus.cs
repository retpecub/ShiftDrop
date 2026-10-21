using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ShiftDrop.Engines;

namespace ShiftDrop.Core;

/// <summary>Наборы действий для радиального меню по типу файла.</summary>
public static class Menus
{
    private static readonly string[] ImageTargets = { "jpg", "png", "webp", "pdf", "bmp", "gif", "tiff", "ico", "avif" };
    private static readonly string[] PdfTargets = { "jpg", "png" };
    private static readonly string[] VideoTargets = { "mp4", "mkv", "webm", "mov", "avi", "gif", "mp3", "wav" };
    private static readonly string[] AudioTargets = { "mp3", "wav", "flac", "ogg", "m4a", "opus" };

    public static List<ActionDef> Get(FileKind kind, bool tools, IReadOnlyList<string> files)
    {
        var srcExt = files.Count > 0 ? Normalize(PathUtil.Ext(files[0])) : "";
        return tools ? Tools(kind, files.Count) : Formats(kind, srcExt);
    }

    private static string Normalize(string ext) => ext switch
    {
        "jpeg" or "jfif" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "oga" => "ogg",
        _ => ext,
    };

    private static List<ActionDef> Formats(FileKind kind, string srcExt)
    {
        IEnumerable<string> targets = kind switch
        {
            FileKind.Image => ImageTargets,
            FileKind.Pdf => PdfTargets,
            FileKind.Video => VideoTargets,
            FileKind.Audio => AudioTargets,
            FileKind.Document or FileKind.Spreadsheet or FileKind.Presentation => OfficeEngine.Targets(kind),
            _ => Enumerable.Empty<string>(),
        };

        return targets
            .Where(t => t != srcExt)
            .Select(t => MakeConvert(kind, t))
            .ToList();
    }

    private static ActionDef MakeConvert(FileKind kind, string target)
    {
        var label = target.ToUpperInvariant();
        return kind switch
        {
            FileKind.Image => new ActionDef
            {
                Label = label,
                Tip = target == "pdf" ? "Каждая картинка — в свой PDF" : $"Конвертировать в {label}",
                PerFile = (ctx, f) => Sync(() => ImageEngine.Convert(ctx, f, target)),
            },
            FileKind.Pdf => new ActionDef
            {
                Label = label,
                Tip = $"Страницы в {label}, 150 dpi",
                PerFile = (ctx, f) => Sync(() => PdfEngine.ToImages(ctx, f, target)),
            },
            FileKind.Video or FileKind.Audio => new ActionDef
            {
                Label = label,
                Tip = target is "mp3" or "wav" && kind == FileKind.Video ? $"Извлечь звук в {label}" : $"Конвертировать в {label}",
                PerFile = (ctx, f) => MediaEngine.Convert(ctx, f, target),
            },
            _ => new ActionDef
            {
                Label = label,
                Tip = $"Конвертировать в {label} ({OfficeEngine.EngineName(kind)})",
                PerFile = (ctx, f) => OfficeEngine.Convert(ctx, f, kind, target),
            },
        };
    }

    private static List<ActionDef> Tools(FileKind kind, int count)
    {
        var list = new List<ActionDef>();
        switch (kind)
        {
            case FileKind.Image:
                list.Add(new ActionDef { Label = "Сжать", Tip = "Уменьшить вес без смены формата", PerFile = (c, f) => Sync(() => ImageEngine.Compress(c, f)) });
                list.Add(new ActionDef { Label = "Без EXIF", Tip = "Удалить метаданные (GPS, камера, дата)", PerFile = (c, f) => Sync(() => ImageEngine.StripMetadata(c, f)) });
                list.Add(new ActionDef { Label = "50%", Tip = "Уменьшить размер вдвое", PerFile = (c, f) => Sync(() => ImageEngine.Resize(c, f, 50)) });
                list.Add(new ActionDef { Label = "Повернуть", Tip = "Повернуть на 90° по часовой", PerFile = (c, f) => Sync(() => ImageEngine.Rotate(c, f, 90)) });
                list.Add(new ActionDef { Label = "Обрезать", Tip = "Выделить область мышью", PerFile = (c, f) => Sync(() => ImageEngine.Crop(c, f)) });
                list.Add(new ActionDef { Label = "В один PDF", Tip = "Все картинки — страницами одного PDF", Batch = (c, fs) => Sync(() => ImageEngine.CombineToPdf(c, fs)) });
                break;

            case FileKind.Pdf:
                list.Add(new ActionDef { Label = "Склеить", Tip = count > 1 ? $"Склеить {count} PDF в один" : "Выберите несколько PDF", Batch = (c, fs) => Sync(() => PdfEngine.Merge(c, fs)) });
                list.Add(new ActionDef { Label = "Разбить", Tip = "Каждая страница — отдельный PDF", PerFile = (c, f) => Sync(() => PdfEngine.Split(c, f)) });
                break;

            case FileKind.Video:
                list.Add(new ActionDef { Label = "Сжать", Tip = "H.264, CRF 28 — заметно легче", PerFile = MediaEngine.CompressVideo });
                list.Add(new ActionDef { Label = "720p", Tip = "Уменьшить до 720p", PerFile = MediaEngine.To720p });
                list.Add(new ActionDef { Label = "Без звука", Tip = "Убрать звуковую дорожку", PerFile = MediaEngine.Mute });
                list.Add(new ActionDef { Label = "Без метаданных", Tip = "Удалить метаданные без перекодирования", PerFile = MediaEngine.StripMetadata });
                break;

            case FileKind.Audio:
                list.Add(new ActionDef { Label = "Сжать", Tip = "MP3 128 kbps", PerFile = MediaEngine.CompressAudio });
                list.Add(new ActionDef { Label = "Без метаданных", Tip = "Удалить теги без перекодирования", PerFile = MediaEngine.StripMetadata });
                break;
        }
        return list;
    }

    private static Task Sync(System.Action work)
    {
        work();
        return Task.CompletedTask;
    }
}
