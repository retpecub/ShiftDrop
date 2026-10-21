using System;
using System.Diagnostics;
using System.Windows.Threading;
using ShiftDrop.UI;

namespace ShiftDrop.Core;

/// <summary>
/// Следит за мышью через глобальный хук. Если зажата ЛКМ, курсор сдвинулся и нажат Shift —
/// под курсором ставится невидимое радиальное окно-приёмник. Если тянут файлы, OLE пришлёт
/// DragEnter и меню проявится; если это не файлы (выделение текста и т.п.) — окно так и останется
/// невидимым и уберётся при отпускании кнопки.
/// </summary>
internal sealed class DragWatcher : IDisposable
{
    private const int Threshold = 12; // px

    private readonly MouseHook _hook = new();
    private readonly RadialWindow _overlay;
    private readonly Dispatcher _ui;
    private readonly uint _pid = (uint)Environment.ProcessId;

    private bool _down;
    private bool _triggered;
    private bool _ownWindow;
    private int _sx, _sy;
    private DispatcherTimer? _hideTimer;

    public bool Enabled { get; set; } = true;

    /// <summary>Клик мышью в любом месте (для закрытия click-меню).</summary>
    public event Action<int, int>? GlobalClick;

    public DragWatcher(RadialWindow overlay, Dispatcher ui)
    {
        _overlay = overlay;
        _ui = ui;
        _hook.LeftDown += OnDown;
        _hook.Moved += OnMove;
        _hook.LeftUp += OnUp;
    }

    public void Start() => _hook.Install();

    private void OnDown(int x, int y)
    {
        _down = true;
        _triggered = false;
        _sx = x;
        _sy = y;
        _ownWindow = IsOwnWindowAt(x, y);
        GlobalClick?.Invoke(x, y);
    }

    private void OnMove(int x, int y)
    {
        if (!_down) return;

        if (_triggered)
        {
            _ui.InvokeAsync(() => _overlay.OnHookMove(x, y), DispatcherPriority.Input);
            return;
        }

        if (!Enabled || _ownWindow) return;
        int dx = x - _sx, dy = y - _sy;
        if (dx * dx + dy * dy < Threshold * Threshold) return;
        if (!Native.IsKeyDown(Native.VK_SHIFT)) return;

        _triggered = true;
        _ui.InvokeAsync(() =>
        {
            _hideTimer?.Stop();
            _overlay.Arm(x, y);
        }, DispatcherPriority.Send);
    }

    private void OnUp(int x, int y)
    {
        _down = false;
        if (!_triggered) return;
        _triggered = false;

        // Drop приходит через OLE чуть позже отпускания кнопки — даём ему время, потом прячем.
        _ui.InvokeAsync(() =>
        {
            _hideTimer?.Stop();
            _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
            {
                _hideTimer?.Stop();
                _overlay.Disarm();
            }, _ui);
            _hideTimer.Start();
        });
    }

    private bool IsOwnWindowAt(int x, int y)
    {
        try
        {
            var hwnd = Native.WindowFromPoint(new Native.POINT(x, y));
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == _pid;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _hook.Dispose();
}
