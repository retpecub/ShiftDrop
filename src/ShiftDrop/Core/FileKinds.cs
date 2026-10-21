using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShiftDrop.Core;

public enum FileKind
{
    Unknown,
    Image,
    Pdf,
    Video,
    Audio,
    Document,
    Spreadsheet,
    Presentation,
    Archive,
    /// <summary>Прочие файлы и папки — только упаковка в архив.</summary>
    Other,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Add(FileKind.Image, "jpg", "jpeg", "jfif", "png", "webp", "bmp", "gif", "tif", "tiff", "ico", "heic", "heif", "avif", "tga", "psd");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Video, "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "ts", "mts");
        Add(FileKind.Audio, "mp3", "wav", "flac", "ogg", "oga", "m4a", "aac", "wma", "opus", "amr", "aiff");
        Add(FileKind.Document, "doc", "docx", "odt", "rtf", "txt");
        Add(FileKind.Spreadsheet, "xls", "xlsx", "ods", "csv");
        Add(FileKind.Presentation, "ppt", "pptx", "odp");
        Add(FileKind.Archive, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "cab", "iso", "zst");
    }

    private static void Add(FileKind kind, params string[] exts)
    {
        foreach (var e in exts) Map[e] = kind;
    }

    public static IEnumerable<string> AllExtensions => Map.Keys;

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.Unknown;
        return Map.TryGetValue(PathUtil.Ext(path), out var k) ? k : FileKind.Unknown;
    }

    /// <summary>
    /// Тип — по первому поддерживаемому файлу; files — только файлы этого типа;
    /// all — всё выделенное (файлы и папки), для упаковки в архив.
    /// Если поддерживаемых нет, но что-то выделено — FileKind.Other.
    /// </summary>
    public static (FileKind kind, List<string> files, List<string> all) Classify(IEnumerable<string> paths)
    {
        var all = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Length > 3 ? p.TrimEnd('\\', '/') : p)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (all.Count == 0) return (FileKind.Unknown, new List<string>(), all);

        var kind = all.Select(Of).FirstOrDefault(k => k != FileKind.Unknown);
        if (kind == FileKind.Unknown) return (FileKind.Other, all, all);
        return (kind, all.Where(p => Of(p) == kind).ToList(), all);
    }

    public static string Title(FileKind kind) => kind switch
    {
        FileKind.Image => "Картинка",
        FileKind.Pdf => "PDF",
        FileKind.Video => "Видео",
        FileKind.Audio => "Аудио",
        FileKind.Document => "Документ",
        FileKind.Spreadsheet => "Таблица",
        FileKind.Presentation => "Презентация",
        FileKind.Archive => "Архив",
        FileKind.Other => "Файлы",
        _ => "Файл",
    };
}
