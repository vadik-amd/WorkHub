namespace WorkHub;

internal static class Program
{
    // WinForms (and OLE file dialogs, shell launches) require the UI thread to be STA.
    [STAThread]
    private static void Main()
    {
        // Single-instance guard: the hub must run only once even if launched again
        // (e.g. autostart + manual launch).
        using var mutex = new Mutex(initiallyOwned: true, name: "Global\\WorkHub_SingleInstance", out bool isNew);
        if (!isNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());

        GC.KeepAlive(mutex);
    }
}
