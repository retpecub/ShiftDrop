using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ShiftDrop.UI;

namespace ShiftDrop.Core;

/// <summary>Действие из радиального меню.</summary>
public sealed class ActionDef
{
    public required string Label { get; init; }

    /// <summary>Подсказка в центре меню при наведении.</summary>
    public string? Tip { get; init; }

    /// <summary>Обработка каждого файла по отдельности.</summary>
    public Func<JobContext, string, Task>? PerFile { get; init; }

    /// <summary>Действию нужны все выделенные элементы (включая папки и файлы других типов) — для архивов.</summary>
    public bool UsesAllItems { get; init; }

    /// <summary>Обработка всех файлов разом (склейка и т.п.).</summary>
    public Func<JobContext, IReadOnlyList<string>, Task>? Batch { get; init; }
}

/// <summary>Контекст выполнения: прогресс, результаты, отмена.</summary>
public sealed class JobContext
{
    private readonly ToastWindow _toast;

    public JobContext(ToastWindow toast, CancellationToken token)
    {
        _toast = toast;
        Token = token;
    }

    public CancellationToken Token { get; }
    public List<string> Outputs { get; } = new();
    public List<string> Errors { get; } = new();

    public void Status(string text) => _toast.SetStatus(text);

    /// <summary>0..1 или null — неопределённый прогресс.</summary>
    public void Progress(double? value) => _toast.SetProgress(value);

    public void AddOutput(string path)
    {
        lock (Outputs) Outputs.Add(path);
    }
}

public static class Jobs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Запуск из UI-потока. Задачи выполняются по очереди.</summary>
    public static async void Start(ActionDef action, IReadOnlyList<string> files)
    {
        var cts = new CancellationTokenSource();
        var toast = new ToastWindow(action.Label, cts);
        toast.Show();
        toast.SetStatus("В очереди…");

        await Gate.WaitAsync();
        var ctx = new JobContext(toast, cts.Token);
        try
        {
            await Task.Run(async () =>
            {
                if (action.Batch != null)
                {
                    ctx.Progress(null);
                    ctx.Status($"{files.Count} {PathUtil.Plural(files.Count, "файл", "файла", "файлов")}…");
                    await action.Batch(ctx, files);
                    return;
                }

                for (int i = 0; i < files.Count; i++)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(files[i]);
                    ctx.Status(files.Count > 1 ? $"{i + 1} из {files.Count}: {name}" : name);
                    ctx.Progress(null);
                    try
                    {
                        await action.PerFile!(ctx, files[i]);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"{action.Label} {files[i]}: {ex}");
                        ctx.Errors.Add($"{name}: {ex.Message}");
                    }
                }
            }, cts.Token);

            toast.Done(ctx.Outputs, ctx.Errors);
        }
        catch (OperationCanceledException)
        {
            toast.Cancelled(ctx.Outputs);
        }
        catch (Exception ex)
        {
            Log.Write($"{action.Label}: {ex}");
            ctx.Errors.Add(ex.Message);
            toast.Done(ctx.Outputs, ctx.Errors);
        }
        finally
        {
            Gate.Release();
        }
    }
}
