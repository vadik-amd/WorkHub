using System.Net.Http;
using System.Timers;
using Timer = System.Timers.Timer;

namespace WorkHub;

/// <summary>
/// Periodically probes general internet connectivity and reports status with debounce.
/// "Down" is declared only after several consecutive failed probes.
/// </summary>
public sealed class InternetMonitor : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly Timer _timer = new() { AutoReset = true };
    private AppSettings _settings;
    private int _consecutiveFailures;
    private bool _isDown;
    private bool _probing;

    /// <summary>(internetUp, downConfirmed) — fired on each probe.</summary>
    public event Action<bool, bool>? Tick;

    public InternetMonitor(AppSettings settings)
    {
        _settings = settings;
        _timer.Elapsed += OnElapsed;
    }

    public void Enable(AppSettings settings)
    {
        _settings = settings;
        _consecutiveFailures = 0;
        _isDown = false;
        _timer.Interval = Math.Max(5, settings.GpProbeIntervalSeconds) * 1000.0;
        _timer.Start();
    }

    public void Disable() => _timer.Stop();

    private async void OnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_probing) return;
        _probing = true;
        try
        {
            bool up = await ProbeAsync();
            if (up)
            {
                _consecutiveFailures = 0;
                _isDown = false;
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= Math.Max(1, _settings.GpFailuresBeforeDown))
                    _isDown = true;
            }
            Tick?.Invoke(up, _isDown);
        }
        catch { /* ignore this tick */ }
        finally { _probing = false; }
    }

    private async Task<bool> ProbeAsync()
    {
        foreach (var url in _settings.GpProbeUrls)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)resp.StatusCode < 400) return true;
            }
            catch { /* try next url */ }
        }
        return false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Elapsed -= OnElapsed;
        _timer.Dispose();
        _http.Dispose();
    }
}
