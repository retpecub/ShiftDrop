using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using ShiftDrop.Core;

namespace ShiftDrop.Engines;

/// <summary>
/// Архивы, как в контекстном меню 7-Zip:
///   «Распаковать в "Имя\"», «Добавить к "Имя.7z"», «Добавить к "Имя.zip"».
/// Если установлен 7-Zip — используется 7z.exe (быстрее, понимает всё, включая RAR5).
/// Без него: ZIP создаётся встроенным System.IO.Compression, распаковка — через SharpCompress;
/// создание .7z без 7-Zip недоступно (пункт не показывается).
/// </summary>
public static class ArchiveEngine
{
    private static readonly string[] MultiExt = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" };
    private static readonly Regex PercentRx = new(@"(\d{1,3})%", RegexOptions.Compiled);

    private static string? _sevenZip;
    private static bool _searched;

    public static string? FindSevenZip()
    {
        if (_searched) return _sevenZip;
        _searched = true;

        var candidates = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "7-Zip", "7z.exe"));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "7z.exe"));

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim('"'), "7z.exe")); } catch { /* ignore */ }
        }

        return _sevenZip = candidates.FirstOrDefault(File.Exists);
    }

    public static bool CanCreate7z => FindSevenZip() != null;

    // ---------------- имена ----------------

    /// <summary>Имя без «архивного» расширения: data.tar.gz → data.</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        foreach (var e in MultiExt)
            if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                return name[..^e.Length];
        return Directory.Exists(path) ? name : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Как у 7-Zip: один элемент — его имя, несколько — имя папки, где они лежат.</summary>
    public static (string dir, string name) ArchiveNameFor(IReadOnlyList<string> items)
    {
        var first = items[0].TrimEnd('\\', '/');
        var dir = Path.GetDirectoryName(first) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (items.Count == 1) return (dir, BaseName(first));
        var parentName = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return (dir, string.IsNullOrEmpty(parentName) ? "Архив" : parentName);
    }

    public static string ExtractDirFor(string archive)
    {
        var parent = Path.GetDirectoryName(archive)!;
        var name = BaseName(archive);
        var candidate = Path.Combine(parent, name);
        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = Path.Combine(parent, $"{name} ({i})");
        return candidate;
    }

    private static string UniqueFile(string dir, string name, string ext)
    {
        var candidate = Path.Combine(dir, $"{name}.{ext}");
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i}).{ext}");
        return candidate;
    }

    // ---------------- распаковка ----------------

    public static async Task Extract(JobContext ctx, string archive)
    {
        var outDir = ExtractDirFor(archive);
        Directory.CreateDirectory(outDir);
        try
        {
            var sz = FindSevenZip();
            if (sz != null)
                await Run7z(ctx, sz, new[] { "x", "-bsp1", "-bb0", "-y", "-o" + outDir, "--", archive });
            else
                await Task.Run(() => ExtractManaged(ctx, archive, outDir));
        }
        catch
        {
            TryDeleteEmptyDir(outDir);
            throw;
        }
        ctx.AddOutput(outDir);
    }

    private static void ExtractManaged(JobContext ctx, string archive, string outDir)
    {
        var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };

        if (PathUtil.Ext(archive) == "7z")
        {
            // 7z нужен произвольный доступ — через ArchiveFactory
            using var arc = ArchiveFactory.Open(archive);
            int total = Math.Max(1, arc.Entries.Count(e => !e.IsDirectory));
            int done = 0;
            using var reader = arc.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                reader.WriteEntryToDirectory(outDir, options);
                ctx.Progress((double)++done / total);
            }
            return;
        }

        // zip, rar, tar, tar.gz, gz, bz2, xz — потоково
        using var fs = File.OpenRead(archive);
        using var r = ReaderFactory.Open(fs);
        while (r.MoveToNextEntry())
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (r.Entry.IsDirectory) continue;
            r.WriteEntryToDirectory(outDir, options);
            if (fs.Length > 0) ctx.Progress((double)fs.Position / fs.Length);
        }
    }

    // ---------------- упаковка ----------------

    public static async Task AddToZip(JobContext ctx, IReadOnlyList<string> items)
    {
        var (dir, name) = ArchiveNameFor(items);
        var dst = UniqueFile(dir, name, "zip");
        ctx.Status($"Добавляю к \"{Path.GetFileName(dst)}\"");
        try
        {
            await Task.Run(() => CreateZipManaged(ctx, items, dst));
        }
        catch
        {
            TryDelete(dst);
            throw;
        }
        ctx.AddOutput(dst);
    }

    public static async Task AddTo7z(JobContext ctx, IReadOnlyList<string> items)
    {
        var sz = FindSevenZip() ?? throw new InvalidOperationException("Для .7z нужен установленный 7-Zip");
        var (dir, name) = ArchiveNameFor(items);
        var dst = UniqueFile(dir, name, "7z");
        ctx.Status($"Добавляю к \"{Path.GetFileName(dst)}\"");

        var args = new List<string> { "a", "-t7z", "-mx=5", "-bsp1", "-bb0", "-y", "--", dst };
        args.AddRange(items.Select(i => i.TrimEnd('\\', '/')));
        try
        {
            await Run7z(ctx, sz, args);
        }
        catch
        {
            TryDelete(dst);
            throw;
        }
        ctx.AddOutput(dst);
    }

    private static void CreateZipManaged(JobContext ctx, IReadOnlyList<string> items, string dst)
    {
        // Собираем список (полный путь → имя внутри архива), папки — вместе с корневой папкой, как в 7-Zip
        var entries = new List<(string full, string name, bool isDir)>();
        foreach (var raw in items)
        {
            var item = raw.TrimEnd('\\', '/');
            if (File.Exists(item))
            {
                entries.Add((item, Path.GetFileName(item), false));
            }
            else if (Directory.Exists(item))
            {
                var baseDir = Path.GetDirectoryName(item) ?? item;
                entries.Add((item, Rel(baseDir, item) + "/", true));
                foreach (var d in Directory.EnumerateDirectories(item, "*", SearchOption.AllDirectories))
                    entries.Add((d, Rel(baseDir, d) + "/", true));
                foreach (var f in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                    entries.Add((f, Rel(baseDir, f), false));
            }
        }

        long totalBytes = Math.Max(1, entries.Where(e => !e.isDir).Sum(e => SafeLength(e.full)));
        long doneBytes = 0;

        using var zip = ZipFile.Open(dst, ZipArchiveMode.Create, Encoding.UTF8);
        foreach (var (full, name, isDir) in entries)
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (isDir)
            {
                zip.CreateEntry(name);
                continue;
            }
            if (string.Equals(full, dst, StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(full, name, CompressionLevel.Optimal);
            doneBytes += SafeLength(full);
            ctx.Progress((double)doneBytes / totalBytes);
        }
    }

    private static string Rel(string baseDir, string path) =>
        Path.GetRelativePath(baseDir, path).Replace('\\', '/');

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ---------------- 7z.exe ----------------

    private static async Task Run7z(JobContext ctx, string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // закрываем сразу: на запароленном архиве 7z не повиснет в ожидании ввода
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Insert(1, "-sccUTF-8");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить 7z.exe");
        proc.StandardInput.Close();

        // Прогресс 7z пишет через \b, без переводов строк — читаем кусками
        var stdoutTask = Task.Run(async () =>
        {
            var buf = new char[512];
            int n;
            while ((n = await proc.StandardOutput.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var matches = PercentRx.Matches(new string(buf, 0, n));
                if (matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out var p))
                    ctx.Progress(Math.Clamp(p / 100.0, 0, 1));
            }
        });
        var stderrTask = proc.StandardError.ReadToEndAsync();

        try
        {
            await proc.WaitForExitAsync(ctx.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            throw;
        }
        await stdoutTask;
        var err = (await stderrTask).Trim();

        // 0 — ок, 1 — предупреждения (например, файл занят), 2+ — ошибка
        if (proc.ExitCode >= 2)
        {
            var msg = string.IsNullOrEmpty(err) ? $"код {proc.ExitCode}" : err.Replace("\r", " ").Replace("\n", " ");
            if (msg.Contains("password", StringComparison.OrdinalIgnoreCase))
                msg = "архив защищён паролем — откройте его в 7-Zip";
            throw new InvalidOperationException("7-Zip: " + msg);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static void TryDeleteEmptyDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch
        {
            // ignore
        }
    }
}
