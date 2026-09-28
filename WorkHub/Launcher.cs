using System.ComponentModel;
using System.Diagnostics;

namespace WorkHub;

/// <summary>Starts configured programs/shortcuts, optionally elevated (UAC).</summary>
public static class Launcher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: user declined the UAC prompt

    public static string DisplayName(LaunchItem item) =>
        string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileName(item.Path) : item.Name;

    public static (bool ok, string? error) Launch(LaunchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
            return (false, "пустой путь");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = item.Path,
                // UseShellExecute lets us launch .lnk shortcuts, documents, and use "runas".
                UseShellExecute = true,
            };

            if (!string.IsNullOrWhiteSpace(item.Arguments))
                psi.Arguments = item.Arguments;

            try
            {
                var dir = Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    psi.WorkingDirectory = dir;
            }
            catch { /* non-fatal */ }

            if (item.RunAsAdmin)
                psi.Verb = "runas";

            Process.Start(psi);
            return (true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (false, "запрос UAC отклонён");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (int launched, int failed, List<string> errors) LaunchAll(IEnumerable<LaunchItem> items)
    {
        int launched = 0, failed = 0;
        var errors = new List<string>();

        foreach (var item in items)
        {
            if (!item.Enabled) continue;
            var (ok, error) = Launch(item);
            if (ok) launched++;
            else
            {
                failed++;
                errors.Add($"{DisplayName(item)}: {error}");
            }
        }

        return (launched, failed, errors);
    }
}
