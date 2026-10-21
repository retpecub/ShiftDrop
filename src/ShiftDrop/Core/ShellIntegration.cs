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

    // Пункт на всех файлах (*) и на папках — чтобы работали архивы для чего угодно
    private static readonly string[] VerbKeys =
    {
        $@"Software\Classes\*\shell\{VerbName}",
        $@"Software\Classes\Directory\shell\{VerbName}",
    };

    // Старый вариант (v0.1): отдельный ключ на каждое расширение
    private static string LegacyVerbKey(string ext) => $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{VerbName}";

    // ---------- контекстное меню ----------

    public static bool IsContextMenuInstalled()
    {
        foreach (var key in VerbKeys)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key + @"\command");
            var cmd = k?.GetValue("") as string;
            if (cmd == null || !cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static void InstallContextMenu()
    {
        RemoveLegacy();
        var exe = ExePath;
        foreach (var key in VerbKeys)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(key);
            verb.SetValue("MUIVerb", "Конвертировать… (ShiftDrop)");
            verb.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --menu \"%1\"");
        }
        NotifyShell();
    }

    public static void UninstallContextMenu()
    {
        RemoveLegacy();
        foreach (var key in VerbKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { /* ignore */ }
        }
        NotifyShell();
    }

    private static void RemoveLegacy()
    {
        foreach (var ext in FileKinds.AllExtensions.Select(e => e.ToLowerInvariant()).Distinct())
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(LegacyVerbKey(ext), false); } catch { /* ignore */ }
        }
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
