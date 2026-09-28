using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorkHub;

/// <summary>
/// Drives the GlobalProtect agent UI to re-establish the tunnel by clicking the top
/// item of its hamburger menu (which is "Refresh Connection" when connected, or
/// "Connect" when disconnected — both re-establish, and both sit at the top so we never
/// risk hitting "Disconnect").
///
/// GlobalProtect's menu is owner-drawn and not exposed to UI Automation, so we click by
/// coordinates anchored to real window rectangles read at run time: the hamburger as a
/// fraction of the GP window rect, and the menu item as a fraction of the popup rect.
/// </summary>
public static class GlobalProtectController
{
    public static async Task<bool> RefreshConnectionAsync(AppSettings s, Action<string> log, CancellationToken ct)
    {
        IntPtr gp = FindWindow(cls => cls == "#32770", title => title == "GlobalProtect");
        if (gp == IntPtr.Zero)
        {
            log("Окно GlobalProtect не найдено — открываю агента…");
            TryLaunchAgent(s);
            for (int i = 0; i < 16 && gp == IntPtr.Zero; i++)
            {
                await Task.Delay(500, ct);
                gp = FindWindow(cls => cls == "#32770", title => title == "GlobalProtect");
            }
        }
        if (gp == IntPtr.Zero) { log("Не удалось открыть окно GlobalProtect."); return false; }

        NativeMethods.GetCursorPos(out var savedCursor);
        try
        {
            NativeMethods.ShowWindow(gp, NativeMethods.SW_RESTORE);
            NativeMethods.BringWindowToTop(gp);
            NativeMethods.SetForegroundWindow(gp);
            await Task.Delay(400, ct);

            if (!NativeMethods.GetWindowRect(gp, out var wr)) { log("Не удалось получить размеры окна."); return false; }
            int wW = wr.Right - wr.Left, wH = wr.Bottom - wr.Top;
            int hamX = wr.Left + (int)(wW * s.GpHamburgerFracX);
            int hamY = wr.Top + (int)(wH * s.GpHamburgerFracY);

            log($"Открываю меню (гамбургер @ {hamX},{hamY})…");
            Click(hamX, hamY);
            await Task.Delay(900, ct);

            IntPtr popup = FindWindow(cls => cls == "#32768", _ => true, mustBeVisible: true);
            if (popup == IntPtr.Zero || !NativeMethods.GetWindowRect(popup, out var pr) || pr.Right <= pr.Left)
            {
                log("Меню не открылось (попап не найден) — ничего не нажимаю.");
                SendEscape();
                return false;
            }

            int itemX = pr.Left + (pr.Right - pr.Left) / 2;
            int itemY = pr.Top + (int)((pr.Bottom - pr.Top) * s.GpMenuTopItemFracY);
            log($"Нажимаю верхний пункт меню (Refresh/Connect) @ {itemX},{itemY}…");
            Click(itemX, itemY);
            await Task.Delay(300, ct);
            return true;
        }
        finally
        {
            NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y);
        }
    }

    private static void TryLaunchAgent(AppSettings s)
    {
        try
        {
            if (File.Exists(s.GpPanGpaPath))
                Process.Start(new ProcessStartInfo { FileName = s.GpPanGpaPath, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private static void Click(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(120);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    private static void SendEscape()
    {
        NativeMethods.keybd_event(NativeMethods.VK_ESCAPE, 0, 0, IntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_ESCAPE, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    private static IntPtr FindWindow(Func<string, bool> classMatch, Func<string, bool> titleMatch, bool mustBeVisible = false)
    {
        IntPtr result = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (mustBeVisible && !NativeMethods.IsWindowVisible(hwnd)) return true;

            var cls = GetText(hwnd, NativeMethods.GetClassName);
            if (!classMatch(cls)) return true;
            var title = GetText(hwnd, NativeMethods.GetWindowText);
            if (!titleMatch(title)) return true;

            result = hwnd;
            return false; // stop
        }, IntPtr.Zero);
        return result;
    }

    private delegate int TextGetter(IntPtr hwnd, System.Text.StringBuilder sb, int max);

    private static string GetText(IntPtr hwnd, TextGetter getter)
    {
        var sb = new System.Text.StringBuilder(512);
        getter(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const byte VK_ESCAPE = 0x1B;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
