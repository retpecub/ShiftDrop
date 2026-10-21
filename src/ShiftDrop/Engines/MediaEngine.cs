using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ShiftDrop.Core;

namespace ShiftDrop.Engines;

/// <summary>Видео и аудио через ffmpeg.exe (скачивается при первом использовании).</summary>
public static class MediaEngine
{
    private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly string[] H264 = { "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k" };

    private static string[] ArgsFor(string target) => target switch
    {
        "mp4" or "mov" => Concat(H264, "-movflags", "+faststart"),
        "mkv" => H264,
        "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k" },
        "avi" => new[] { "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "4" },
        "gif" => new[] { "-vf", "fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0", "-an" },
        "mp3" => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        "wav" => new[] { "-vn", "-c:a", "pcm_s16le" },
        "flac" => new[] { "-vn", "-c:a", "flac" },
        "ogg" => new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
        "m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
        "opus" => new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" },
        _ => throw new NotSupportedException("Формат не поддерживается: " + target),
    };

    public static Task Convert(JobContext ctx, string src, string target)
        => Run(ctx, src, PathUtil.Output(src, target), ArgsFor(target));

    public static Task CompressVideo(JobContext ctx, string src)
        => Run(ctx, src, PathUtil.Output(src, "mp4", "_compressed"),
            new[] { "-c:v", "libx264", "-preset", "slow", "-crf", "28", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" });

    public static Task To720p(JobContext ctx, string src)
        => Run(ctx, src, PathUtil.Output(src, "mp4", "_720p"),
            Concat(new[] { "-vf", "scale=-2:'min(720,ih)'" }, Concat(H264, "-movflags", "+faststart")));

    public static Task Mute(JobContext ctx, string src)
        => Run(ctx, src, PathUtil.Output(src, PathUtil.Ext(src), "_mute"), new[] { "-map", "0:v", "-c", "copy", "-an" });

    public static Task StripMetadata(JobContext ctx, string src)
        => Run(ctx, src, PathUtil.Output(src, PathUtil.Ext(src), "_clean"), new[] { "-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy" });

    public static Task CompressAudio(JobContext ctx, string src)
        => Run(ctx, src, PathUtil.Output(src, "mp3", "_compressed"), new[] { "-vn", "-c:a", "libmp3lame", "-b:a", "128k" });

    private static async Task Run(JobContext ctx, string src, string dst, IEnumerable<string> args)
    {
        var ffmpeg = await Ffmpeg.EnsureAsync(ctx);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-i", src }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(dst);

        double duration = 0;
        var tail = new Queue<string>();

        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > 8) tail.Dequeue();
            }
            var d = DurationRx.Match(e.Data);
            if (d.Success && duration <= 0) duration = Seconds(d);
            var t = TimeRx.Match(e.Data);
            if (t.Success && duration > 0) ctx.Progress(Math.Clamp(Seconds(t) / duration, 0, 1));
        };
        proc.OutputDataReceived += (_, _) => { };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        try
        {
            await proc.WaitForExitAsync(ctx.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* уже завершён */ }
            TryDelete(dst);
            throw;
        }

        if (proc.ExitCode != 0)
        {
            TryDelete(dst);
            string msg;
            lock (tail) msg = string.Join(" | ", tail);
            throw new InvalidOperationException("ffmpeg: " + msg);
        }
        ctx.AddOutput(dst);
    }

    private static double Seconds(Match m) =>
        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
        double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

    private static string[] Concat(string[] a, params string[] b)
    {
        var r = new string[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
