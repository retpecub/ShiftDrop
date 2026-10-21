using System;
using System.IO;

namespace ShiftDrop.Core;

internal static class Log
{
    private static readonly object Sync = new();

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShiftDrop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FilePath => Path.Combine(DataDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // лог — не критичен
        }
    }
}
