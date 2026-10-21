using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ShiftDrop.Core;

namespace ShiftDrop.Engines;

/// <summary>
/// Офисные документы: сначала MS Office (через COM), если его нет — LibreOffice в headless-режиме.
/// </summary>
public static class OfficeEngine
{
    private static readonly string[] DocTargets = { "pdf", "docx", "odt", "rtf", "txt" };
    private static readonly string[] SheetTargets = { "pdf", "xlsx", "ods", "csv" };
    private static readonly string[] SlideTargets = { "pdf", "pptx", "odp" };

    private static string? _soffice;
    private static bool _sofficeSearched;

    public static IEnumerable<string> Targets(FileKind kind)
    {
        if (EngineName(kind) == null) return Enumerable.Empty<string>();
        return kind switch
        {
            FileKind.Document => DocTargets,
            FileKind.Spreadsheet => SheetTargets,
            FileKind.Presentation => SlideTargets,
            _ => Enumerable.Empty<string>(),
        };
    }

    /// <summary>Чем будем конвертировать: "MS Office", "LibreOffice" или null.</summary>
    public static string? EngineName(FileKind kind)
    {
        if (MsProgId(kind) is { } progId && Type.GetTypeFromProgID(progId) != null) return "MS Office";
        if (FindSoffice() != null) return "LibreOffice";
        return null;
    }

    private static string? MsProgId(FileKind kind) => kind switch
    {
        FileKind.Document => "Word.Application",
        FileKind.Spreadsheet => "Excel.Application",
        FileKind.Presentation => "PowerPoint.Application",
        _ => null,
    };

    public static string? FindSoffice()
    {
        if (_sofficeSearched) return _soffice;
        _sofficeSearched = true;
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "LibreOffice*"))
                {
                    var exe = Path.Combine(dir, "program", "soffice.exe");
                    if (File.Exists(exe)) return _soffice = exe;
                }
            }
            catch
            {
                // нет доступа к папке
            }
        }
        return null;
    }

    public static async Task Convert(JobContext ctx, string src, FileKind kind, string target)
    {
        var dst = PathUtil.Output(src, target);
        var engine = EngineName(kind) ?? throw new InvalidOperationException("Нужен MS Office или LibreOffice");

        if (engine == "MS Office")
        {
            try
            {
                await RunSta(() => ConvertMsOffice(src, dst, kind, target));
                ctx.AddOutput(dst);
                return;
            }
            catch (Exception ex) when (FindSoffice() != null)
            {
                Log.Write("MS Office failed, fallback to LibreOffice: " + ex.Message);
            }
        }

        await ConvertLibreOffice(ctx, src, dst, kind, target);
        ctx.AddOutput(dst);
    }

    // ---------- MS Office (позднее связывание через dynamic, без interop-сборок) ----------

    private static void ConvertMsOffice(string src, string dst, FileKind kind, string target)
    {
        var type = Type.GetTypeFromProgID(MsProgId(kind)!)!;
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            switch (kind)
            {
                case FileKind.Document:
                {
                    app.Visible = false;
                    app.DisplayAlerts = 0; // wdAlertsNone
                    dynamic doc = app.Documents.Open(src, false, true, false); // ConfirmConversions, ReadOnly, AddToRecentFiles
                    try
                    {
                        int fmt = target switch
                        {
                            "pdf" => 17,  // wdFormatPDF
                            "docx" => 16, // wdFormatXMLDocument
                            "odt" => 23,  // wdFormatOpenDocumentText
                            "rtf" => 6,   // wdFormatRTF
                            "txt" => 7,   // wdFormatUnicodeText
                            _ => throw new NotSupportedException(target),
                        };
                        doc.SaveAs2(dst, fmt);
                    }
                    finally
                    {
                        doc.Close(false);
                        Marshal.FinalReleaseComObject(doc);
                    }
                    app.Quit(false);
                    break;
                }
                case FileKind.Spreadsheet:
                {
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    dynamic wb = app.Workbooks.Open(src, 0, true); // UpdateLinks, ReadOnly
                    try
                    {
                        if (target == "pdf")
                        {
                            wb.ExportAsFixedFormat(0, dst); // xlTypePDF
                        }
                        else
                        {
                            int fmt = target switch
                            {
                                "xlsx" => 51, // xlOpenXMLWorkbook
                                "ods" => 60,  // xlOpenDocumentSpreadsheet
                                "csv" => 62,  // xlCSVUTF8
                                _ => throw new NotSupportedException(target),
                            };
                            wb.SaveAs(dst, fmt);
                        }
                    }
                    finally
                    {
                        wb.Close(false);
                        Marshal.FinalReleaseComObject(wb);
                    }
                    app.Quit();
                    break;
                }
                case FileKind.Presentation:
                {
                    dynamic pres = app.Presentations.Open(src, -1, 0, 0); // ReadOnly, Untitled, WithWindow
                    try
                    {
                        int fmt = target switch
                        {
                            "pdf" => 32,  // ppSaveAsPDF
                            "pptx" => 24, // ppSaveAsOpenXMLPresentation
                            "odp" => 35,  // ppSaveAsOpenDocumentPresentation
                            _ => throw new NotSupportedException(target),
                        };
                        pres.SaveAs(dst, fmt);
                    }
                    finally
                    {
                        pres.Close();
                        Marshal.FinalReleaseComObject(pres);
                    }
                    app.Quit();
                    break;
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(app);
        }
        if (!File.Exists(dst)) throw new InvalidOperationException("Office не создал файл");
    }

    private static Task RunSta(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    // ---------- LibreOffice ----------

    private static string LoFilter(FileKind kind, string target) => (kind, target) switch
    {
        (_, "pdf") => "pdf",
        (FileKind.Document, "docx") => "docx:MS Word 2007 XML",
        (FileKind.Document, "txt") => "txt:Text (encoded):UTF8",
        (FileKind.Spreadsheet, "xlsx") => "xlsx:Calc MS Excel 2007 XML",
        (FileKind.Spreadsheet, "csv") => "csv:Text - txt - csv (StarCalc):59,34,76", // ';' и UTF-8 — удобно для русского Excel
        (FileKind.Presentation, "pptx") => "pptx:Impress MS PowerPoint 2007 XML",
        _ => target,
    };

    private static async Task ConvertLibreOffice(JobContext ctx, string src, string dst, FileKind kind, string target)
    {
        var soffice = FindSoffice() ?? throw new InvalidOperationException("LibreOffice не найден");
        var outDir = Path.Combine(Path.GetTempPath(), "ShiftDrop-lo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        // Отдельный профиль, чтобы не конфликтовать с открытым у пользователя LibreOffice
        var profile = new Uri(Path.Combine(Path.GetTempPath(), "ShiftDrop-lo-profile")).AbsoluteUri;

        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-env:UserInstallation=" + profile, "--headless", "--norestore", "--nologo", "--nodefault",
                         "--convert-to", LoFilter(kind, target), "--outdir", outDir, src })
                psi.ArgumentList.Add(a);

            ctx.Progress(null);
            using var proc = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                ctx.Token.ThrowIfCancellationRequested();
                throw new TimeoutException("LibreOffice не ответил за 5 минут");
            }

            var produced = Directory.EnumerateFiles(outDir).FirstOrDefault()
                           ?? throw new InvalidOperationException("LibreOffice не создал файл (код " + proc.ExitCode + ")");
            File.Move(produced, dst);
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* ignore */ }
        }
    }
}
