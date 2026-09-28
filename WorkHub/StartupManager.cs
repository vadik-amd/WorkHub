using Microsoft.Win32;

namespace WorkHub;

/// <summary>Manages "run at Windows startup" via the per-user Run registry key (no admin needed).</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WorkHub";

    private static string ExePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrEmpty(value);
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, $"\"{ExePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void Set(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    /// <summary>
    /// Points an existing autostart entry at the exe that is running now. The publish folder
    /// moves whenever the target framework changes, and a stale entry would silently keep
    /// launching an old build at logon. Does nothing when autostart is off.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string value || value.Length == 0) return;

            string current = $"\"{ExePath}\"";
            if (!string.Equals(value.Trim(), current, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, current);
        }
        catch { /* autostart bookkeeping must never break startup */ }
    }
}
