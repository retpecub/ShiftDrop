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

    /// <summary>Тип определяется по первому поддерживаемому файлу; берутся только файлы того же типа.</summary>
    public static (FileKind kind, List<string> files) Classify(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kind = list.Select(Of).FirstOrDefault(k => k != FileKind.Unknown);
        if (kind == FileKind.Unknown) return (FileKind.Unknown, new List<string>());
        return (kind, list.Where(p => Of(p) == kind).ToList());
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
        _ => "Файл",
    };
}
