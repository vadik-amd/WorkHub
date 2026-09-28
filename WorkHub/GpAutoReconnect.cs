namespace WorkHub;

/// <summary>
/// Watches internet connectivity and, when it stays down, asks GlobalProtect to
/// re-establish the tunnel — rate-limited (cooldown + back-off) so it never thrashes.
/// </summary>
public sealed class GpAutoReconnect : IDisposable
{
    private readonly InternetMonitor _monitor;
    private AppSettings _settings;

    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private int _attempts;
    private bool _busy;

    /// <summary>Human-readable status line for the tray (short).</summary>
    public event Action<string>? Status;
    /// <summary>Notable event worth a balloon: (title, message, isWarning).</summary>
    public event Action<string, string, bool>? Notify;

    public bool Enabled { get; private set; }

    public GpAutoReconnect(AppSettings settings)
    {
        _settings = settings;
        _monitor = new InternetMonitor(settings);
        _monitor.Tick += OnTick;
    }

    public void Enable(AppSettings settings)
    {
        _settings = settings;
        Enabled = true;
        _attempts = 0;
        _lastAttemptUtc = DateTime.MinValue;
        _monitor.Enable(settings);
        Status?.Invoke("GlobalProtect: слежу за интернетом");
    }

    public void Disable()
    {
        Enabled = false;
        _monitor.Disable();
        Status?.Invoke("GlobalProtect: авто-реконнект выключен");
    }

    /// <summary>Manual one-off refresh (from the tray), bypassing the connectivity check.</summary>
    public async Task<bool> ReconnectNowAsync()
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            Status?.Invoke("GlobalProtect: обновляю подключение…");
            bool ok = await GlobalProtectController.RefreshConnectionAsync(
                _settings, m => Status?.Invoke("GP: " + m), CancellationToken.None);
            Notify?.Invoke("GlobalProtect",
                ok ? "Команда обновления подключения отправлена." : "Не удалось обновить подключение.", !ok);
            return ok;
        }
        finally { _busy = false; }
    }

    private void OnTick(bool internetUp, bool downConfirmed)
    {
        if (internetUp)
        {
            if (_attempts > 0)
            {
                _attempts = 0;
                Status?.Invoke("GlobalProtect: интернет в порядке");
            }
            return;
        }

        if (!downConfirmed || _busy) return;

        // Cooldown, widened after several attempts with no recovery.
        double cooldown = _settings.GpReconnectCooldownSeconds;
        if (_attempts >= _settings.GpMaxAttemptsBeforeBackoff)
            cooldown *= 4;

        if ((DateTime.UtcNow - _lastAttemptUtc).TotalSeconds < cooldown)
            return;

        _lastAttemptUtc = DateTime.UtcNow;
        _attempts++;
        _ = TryReconnectAsync(_attempts);
    }

    private async Task TryReconnectAsync(int attempt)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Status?.Invoke($"GlobalProtect: интернет пропал → реконнект (попытка {attempt})…");
            bool ok = await GlobalProtectController.RefreshConnectionAsync(
                _settings, _ => { }, CancellationToken.None);
            Notify?.Invoke("GlobalProtect",
                ok
                    ? $"Интернет пропал — отправил обновление подключения (попытка {attempt})."
                    : $"Реконнект не удался (попытка {attempt}). Проверьте GlobalProtect.",
                !ok);
        }
        catch (Exception ex)
        {
            Notify?.Invoke("GlobalProtect", "Ошибка реконнекта: " + ex.Message, true);
        }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        _monitor.Tick -= OnTick;
        _monitor.Dispose();
    }
}
