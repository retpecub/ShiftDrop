using System;
using System.IO;

namespace ShiftDrop.Core;

internal static class PathUtil
{
    /// <summary>
    /// Путь для результата рядом с исходником: "имя{suffix}.ext", при занятости — "имя{suffix} (2).ext".
    /// </summary>
    public static string Output(string source, string ext, string suffix = "")
    {
        var dir = Path.GetDirectoryName(source);
        if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var name = Path.GetFileNameWithoutExtension(source) + suffix;
        ext = ext.TrimStart('.').ToLowerInvariant();

        var candidate = Path.Combine(dir, $"{name}.{ext}");
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i}).{ext}");
        return candidate;
    }

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }
}
