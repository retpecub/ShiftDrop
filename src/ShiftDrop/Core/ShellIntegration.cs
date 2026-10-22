using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace ShiftDrop.Core;

public enum ContextMenuMode
{
    Off = 0,
    ConvertOnly = 1,
    All = 2,
}

/// <summary>Пункт «Конвертировать…» в контекстном меню Проводника и автозапуск. Всё в HKCU, без прав администратора.</summary>
internal static class ShellIntegration
{
    private const string VerbName = "ShiftDrop";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\ShiftDrop";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath is null");

    // Режим «Для всех файлов и папок»: ключи на * и на Directory
    private static readonly string[] AllKeys =
    {
        $@"Software\Classes\*\shell\{VerbName}",
        $@"Software\Classes\Directory\shell\{VerbName}",
    };

    // Режим «Только для конвертации»: отдельный ключ на каждое расширение (кроме архивов)
    private static string ExtKey(string ext) => $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{VerbName}";

    private static IEnumerable<string> ConvertibleExtensions =>
        FileKinds.AllExtensions
            .Where(e => FileKinds.Of("x." + e) != FileKind.Archive)
            .Select(e => e.ToLowerInvariant())
            .Distinct();

    // ---------- контекстное меню ----------

    public static ContextMenuMode GetContextMenuMode()
    {
        using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
        if (k?.GetValue("ContextMenuMode") is int v && Enum.IsDefined(typeof(ContextMenuMode), v))
            return (ContextMenuMode)v;
        // Миграция со старого флажка: был включён — ставим щадящий режим
        return GetFlag("ContextMenu", true) ? ContextMenuMode.ConvertOnly : ContextMenuMode.Off;
    }

    public static void SetContextMenuMode(ContextMenuMode mode)
    {
        RemoveAll();
        var exe = ExePath;
        IEnumerable<string> keys = mode switch
        {
            ContextMenuMode.All => AllKeys,
            ContextMenuMode.ConvertOnly => ConvertibleExtensions.Select(ExtKey),
            _ => Enumerable.Empty<string>(),
        };
        foreach (var key in keys)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(key);
            verb.SetValue("MUIVerb", "Конвертировать… (ShiftDrop)");
            verb.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --menu \"%1\"");
        }

        using (var s = Registry.CurrentUser.CreateSubKey(SettingsKey))
            s.SetValue("ContextMenuMode", (int)mode, RegistryValueKind.DWord);
        NotifyShell();
    }

    /// <summary>Записи в реестре соответствуют режиму и текущему пути к exe.</summary>
    public static bool IsContextMenuUpToDate(ContextMenuMode mode)
    {
        return mode switch
        {
            ContextMenuMode.Off => !HasVerb(AllKeys[0]) && !HasVerb(ExtKey("jpg")),
            ContextMenuMode.All => AllKeys.All(PointsToMe) && !HasVerb(ExtKey("jpg")),
            ContextMenuMode.ConvertOnly => PointsToMe(ExtKey("jpg")) && !HasVerb(AllKeys[0]) && !HasVerb(ExtKey("zip")),
            _ => true,
        };
    }

    private static bool HasVerb(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k != null;
    }

    private static bool PointsToMe(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key + @"\command");
        return k?.GetValue("") is string cmd && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveAll()
    {
        foreach (var key in AllKeys.Concat(FileKinds.AllExtensions.Select(e => ExtKey(e.ToLowerInvariant())).Distinct()))
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { /* ignore */ }
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
