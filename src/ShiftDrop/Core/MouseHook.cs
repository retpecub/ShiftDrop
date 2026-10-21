using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ShiftDrop.Core;

/// <summary>
/// Глобальный низкоуровневый хук мыши (WH_MOUSE_LL).
/// Обработчики вызываются прямо из хука на UI-потоке — они должны быть очень быстрыми.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private readonly Native.LowLevelMouseProc _proc; // держим ссылку, иначе GC соберёт делегат
    private IntPtr _hook;

    public event Action<int, int>? LeftDown;
    public event Action<int, int>? LeftUp;
    public event Action<int, int>? Moved;

    public MouseHook()
    {
        _proc = HookProc;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось установить хук мыши");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                switch ((int)wParam)
                {
                    case Native.WM_LBUTTONDOWN: LeftDown?.Invoke(data.pt.X, data.pt.Y); break;
                    case Native.WM_LBUTTONUP: LeftUp?.Invoke(data.pt.X, data.pt.Y); break;
                    case Native.WM_MOUSEMOVE: Moved?.Invoke(data.pt.X, data.pt.Y); break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("hook: " + ex);
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
