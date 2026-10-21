using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ShiftDrop.Core;

namespace ShiftDrop.Engines;

/// <summary>Поиск ffmpeg.exe и скачивание сборки BtbN с GitHub при первом запуске видео/аудио.</summary>
public static class Ffmpeg
{
    public const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public static string InstallPath => Path.Combine(Log.DataDir, "ffmpeg", "ffmpeg.exe");

    public static string? Find()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(nearExe)) return nearExe;
        if (File.Exists(InstallPath)) return InstallPath;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // кривые записи в PATH
            }
        }
        return null;
    }

    public static async Task<string> EnsureAsync(JobContext? ctx, bool askUser = true)
    {
        var found = Find();
        if (found != null) return found;

        await DownloadGate.WaitAsync();
        try
        {
            found = Find();
            if (found != null) return found;

            if (askUser)
            {
                var answer = Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                    "Для видео и аудио нужен ffmpeg (около 150 МБ).\n\nСкачать его с GitHub сейчас?\n" +
                    "Он будет сохранён в " + Path.GetDirectoryName(InstallPath),
                    "ShiftDrop — нужен ffmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question));
                if (answer != MessageBoxResult.Yes)
                    throw new OperationCanceledException("Без ffmpeg видео и аудио не конвертируются");
            }

            await DownloadAsync(ctx, ctx?.Token ?? CancellationToken.None);
            return InstallPath;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task DownloadAsync(JobContext? ctx, CancellationToken token)
    {
        var dir = Path.GetDirectoryName(InstallPath)!;
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(Path.GetTempPath(), "shiftdrop-ffmpeg.zip");

        ctx?.Status("Скачиваю ffmpeg…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ShiftDrop/0.1");
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;

            await using var input = await resp.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) ctx?.Progress((double)done / total);
            }
        }

        ctx?.Status("Распаковываю ffmpeg…");
        ctx?.Progress(null);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new InvalidOperationException("В архиве не найден ffmpeg.exe");
            entry.ExtractToFile(InstallPath, overwrite: true);
        }
        try { File.Delete(zipPath); } catch { /* ignore */ }
    }
}
