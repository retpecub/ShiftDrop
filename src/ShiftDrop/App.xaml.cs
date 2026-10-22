using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ShiftDrop.Core;
using ShiftDrop.Engines;
using ShiftDrop.UI;
using WinForms = System.Windows.Forms;

namespace ShiftDrop;

public partial class App : Application
{
    private Mutex? _mutex;
    private readonly CancellationTokenSource _ipcCts = new();
    private RadialWindow? _overlay;
    private DragWatcher? _watcher;
    private WinForms.NotifyIcon? _tray;
    private RadialWindow? _clickMenu;

    private readonly List<string> _pendingMenuFiles = new();
    private DispatcherTimer? _menuDebounce;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, @"Local\ShiftDrop.Main." + Environment.UserName, out bool isFirst);
        if (!isFirst)
        {
            bool manualStart = e.Args.Length == 0;
            if (manualStart && ReplaceRunningInstance())
            {
                // Старый экземпляр закрыт — продолжаем как основной (так обновляется версия).
                Log.Write("replaced running instance");
            }
            else
            {
                // Уже запущен: передаём аргументы основному процессу и выходим.
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                if (!e.Args.Contains("--background"))
                    Ipc.TrySend(e.Args, TimeSpan.FromSeconds(5));
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("unhandled: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("fatal: " + ex.ExceptionObject);

        Ipc.StartServer(args => Dispatcher.InvokeAsync(() => HandleArgs(args, remote: true)), _ipcCts.Token);

        _overlay = new RadialWindow(RadialWindow.RadialMode.Drag);
        _watcher = new DragWatcher(_overlay, Dispatcher);
        _watcher.Enabled = ShellIntegration.GetFlag("Enabled", true);
        _watcher.GlobalClick += (x, y) => Dispatcher.InvokeAsync(() => _clickMenu?.OnOutsideClick(x, y));
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Write("hook: " + ex);
            MessageBox.Show("Не удалось перехватить мышь: " + ex.Message, "ShiftDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        FirstRunSetup();
        CreateTray();
        HandleArgs(e.Args, remote: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipcCts.Cancel();
        _watcher?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        try { _mutex?.ReleaseMutex(); } catch { /* не владеем */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Ручной запуск exe при уже работающем ShiftDrop: закрываем старый экземпляр (возможно, старой версии)
    /// и становимся основным. Новые версии понимают --quit; старые просто завершаем.
    /// </summary>
    private bool ReplaceRunningInstance()
    {
        try
        {
            Ipc.TrySend(new[] { "--quit" }, TimeSpan.FromSeconds(1.5));

            var me = Environment.ProcessId;
            var others = Process.GetProcessesByName("ShiftDrop").Where(p => p.Id != me).ToList();
            foreach (var p in others)
            {
                try
                {
                    if (!p.WaitForExit(2500)) p.Kill();
                    p.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    Log.Write("replace: " + ex.Message);
                }
            }

            try
            {
                return _mutex!.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                return true; // процесс убит — мьютекс брошен, но теперь наш
            }
        }
        catch (Exception ex)
        {
            Log.Write("replace failed: " + ex);
            return false;
        }
    }

    // ---------------- аргументы командной строки ----------------

    private void HandleArgs(string[] args, bool remote)
    {
        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (files.Count > 0)
        {
            // Проводник запускает по процессу на каждый выделенный файл — собираем их в пачку.
            _pendingMenuFiles.AddRange(files);
            _menuDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) => ShowPendingMenu(), Dispatcher);
            _menuDebounce.Stop();
            _menuDebounce.Start();
            return;
        }

        if (remote && args.Contains("--quit"))
        {
            Shutdown();
            return;
        }

        if (remote && args.Contains("--show"))
            ToastWindow.Info("ShiftDrop уже работает", "Зажмите Shift и перетащите файл — появится меню.\nCtrl во время перетаскивания — инструменты.");
    }

    private void ShowPendingMenu()
    {
        _menuDebounce?.Stop();
        var files = _pendingMenuFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingMenuFiles.Clear();
        if (files.Count == 0) return;

        _clickMenu?.Close();
        Native.GetCursorPos(out var pt);
        var menu = new RadialWindow(RadialWindow.RadialMode.Click);
        menu.Closed += (_, _) => { if (_clickMenu == menu) _clickMenu = null; };
        _clickMenu = menu;
        menu.ShowForClick(files, pt.X, pt.Y);
    }

    // ---------------- первый запуск ----------------

    private void FirstRunSetup()
    {
        if (ShellIntegration.GetFlag("Configured", false))
        {
            // Если exe переместили — обновим пути в реестре.
            try
            {
                if (ShellIntegration.GetFlag("ContextMenu", true) && !ShellIntegration.IsContextMenuInstalled())
                    ShellIntegration.InstallContextMenu();
                if (ShellIntegration.GetFlag("Autostart", true) && !ShellIntegration.IsAutostartEnabled())
                    ShellIntegration.SetAutostart(true);
            }
            catch (Exception ex)
            {
                Log.Write("refresh registry: " + ex.Message);
            }
            return;
        }

        try
        {
            ShellIntegration.InstallContextMenu();
            ShellIntegration.SetAutostart(true);
            ShellIntegration.SetFlag("ContextMenu", true);
            ShellIntegration.SetFlag("Autostart", true);
            ShellIntegration.SetFlag("Configured", true);
        }
        catch (Exception ex)
        {
            Log.Write("first run: " + ex);
        }

        ToastWindow.Info("ShiftDrop запущен",
            "Зажмите Shift и перетащите файл — появится круговое меню форматов.\n" +
            "Ctrl во время перетаскивания — инструменты (сжать, обрезать, склеить…).\n" +
            "Также: правый клик по файлу → «Конвертировать…».\nНастройки — в значке в трее.", 12);
    }

    // ---------------- трей ----------------

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        try
        {
            icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new WinForms.ContextMenuStrip();

        var header = new WinForms.ToolStripMenuItem($"ShiftDrop {Version} — Shift + перетаскивание") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var enabled = new WinForms.ToolStripMenuItem("Включено") { Checked = _watcher?.Enabled ?? true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) =>
        {
            if (_watcher != null) _watcher.Enabled = enabled.Checked;
            ShellIntegration.SetFlag("Enabled", enabled.Checked);
        };
        menu.Items.Add(enabled);

        var ctxMenu = new WinForms.ToolStripMenuItem("Пункт в меню Проводника") { Checked = SafeGet(ShellIntegration.IsContextMenuInstalled), CheckOnClick = true };
        ctxMenu.CheckedChanged += (_, _) =>
        {
            try
            {
                if (ctxMenu.Checked) ShellIntegration.InstallContextMenu();
                else ShellIntegration.UninstallContextMenu();
                ShellIntegration.SetFlag("ContextMenu", ctxMenu.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ShiftDrop");
            }
        };
        menu.Items.Add(ctxMenu);

        var autostart = new WinForms.ToolStripMenuItem("Запускать с Windows") { Checked = SafeGet(ShellIntegration.IsAutostartEnabled), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            try
            {
                ShellIntegration.SetAutostart(autostart.Checked);
                ShellIntegration.SetFlag("Autostart", autostart.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ShiftDrop");
            }
        };
        menu.Items.Add(autostart);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var ffmpegItem = new WinForms.ToolStripMenuItem();
        void RefreshFfmpeg()
        {
            var path = Ffmpeg.Find();
            ffmpegItem.Text = path != null ? "ffmpeg: найден" : "Скачать ffmpeg (для видео/аудио)";
            ffmpegItem.Enabled = path == null;
        }
        ffmpegItem.Click += (_, _) => Dispatcher.InvokeAsync(() => Jobs.Start(new ActionDef
        {
            Label = "Загрузка ffmpeg",
            Batch = async (ctx, _) => { ctx.AddOutput(await Ffmpeg.EnsureAsync(ctx, askUser: false)); },
        }, Array.Empty<string>()));
        menu.Opening += (_, _) => RefreshFfmpeg();
        RefreshFfmpeg();
        menu.Items.Add(ffmpegItem);

        var office = new WinForms.ToolStripMenuItem("Офис: " + (OfficeEngine.EngineName(FileKind.Document) ?? "не найден (нужен MS Office или LibreOffice)")) { Enabled = false };
        menu.Items.Add(office);

        menu.Items.Add("Открыть журнал", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Log.DataDir) { UseShellExecute = true }); } catch { /* ignore */ }
        });

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.InvokeAsync(() => Shutdown()));

        _tray = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "ShiftDrop — Shift + перетащить файл",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == WinForms.MouseButtons.Left)
                Dispatcher.InvokeAsync(() => ToastWindow.Info("ShiftDrop",
                    "Shift + перетащить файл — форматы.\nShift + Ctrl — инструменты.\nПравый клик по файлу → «Конвертировать…»."));
        };
    }

    private static string Version
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static bool SafeGet(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }
}
