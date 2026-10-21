using System;
using System.Linq;
using Microsoft.Win32;

namespace ShiftDrop.Core;

/// <summary>Пункт «Конвертировать…» в контекстном меню Проводника и автозапуск. Всё в HKCU, без прав администратора.</summary>
internal static class ShellIntegration
{
    private const string VerbName = "ShiftDrop";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\ShiftDrop";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath is null");

    private static string VerbKey(string ext) => $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{VerbName}";

    // ---------- контекстное меню ----------

    public static bool IsContextMenuInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(VerbKey("jpg") + @"\command");
        var cmd = k?.GetValue("") as string;
        return cmd != null && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void InstallContextMenu()
    {
        var exe = ExePath;
        foreach (var ext in FileKinds.AllExtensions.Select(e => e.ToLowerInvariant()).Distinct())
        {
            using var verb = Registry.CurrentUser.CreateSubKey(VerbKey(ext));
            verb.SetValue("MUIVerb", "Конвертировать… (ShiftDrop)");
            verb.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --menu \"%1\"");
        }
        NotifyShell();
    }

    public static void UninstallContextMenu()
    {
        foreach (var ext in FileKinds.AllExtensions.Select(e => e.ToLowerInvariant()).Distinct())
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(VerbKey(ext), false); } catch { /* ignore */ }
        }
        NotifyShell();
    }

    private static void NotifyShell()
    {
        try { Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero); } catch { /* ignore */ }
    }

    // ---------- автозапуск ----------

    public static bool IsAutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(VerbName) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutostart(bool enable)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) k.SetValue(VerbName, $"\"{ExePath}\" --background");
        else k.DeleteValue(VerbName, false);
    }

    // ---------- настройки ----------

    public static bool GetFlag(string name, bool def)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return k?.GetValue(name) is int v ? v != 0 : def;
    }

    public static void SetFlag(string name, bool value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
        k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}
