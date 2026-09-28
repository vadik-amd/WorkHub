using System.Timers;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Timer = System.Timers.Timer;

namespace WorkHub;

/// <summary>
/// Polls all active capture endpoints and reports when the microphone is being used
/// by any process other than us (i.e. a call app opened the mic). Stopping is
/// debounced so short pauses / device reconnects don't cut the recording.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    private readonly Timer _timer;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly TimeSpan _stopGrace = TimeSpan.FromSeconds(4);

    private bool _callActive;
    private DateTime? _idleSince;

    public event EventHandler? CallStarted;
    public event EventHandler? CallStopped;

    public bool Enabled { get; private set; }

    public MicMonitor(double pollIntervalMs = 1000)
    {
        _timer = new Timer(pollIntervalMs) { AutoReset = true };
        _timer.Elapsed += OnTick;
    }

    public void Enable()
    {
        Enabled = true;
        _idleSince = null;
        _callActive = false;
        _timer.Start();
    }

    public void Disable()
    {
        Enabled = false;
        _timer.Stop();
        _callActive = false;
        _idleSince = null;
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        bool inUse;
        try
        {
            inUse = IsMicInUseByOthers();
        }
        catch
        {
            // Transient COM/device errors: ignore this tick.
            return;
        }

        if (inUse)
        {
            _idleSince = null;
            if (!_callActive)
            {
                _callActive = true;
                CallStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_callActive)
        {
            _idleSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _idleSince >= _stopGrace)
            {
                _callActive = false;
                _idleSince = null;
                CallStopped?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool IsMicInUseByOthers()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive)
                        continue;

                    uint pid;
                    try { pid = session.GetProcessID; }
                    catch { continue; }

                    if (pid != 0 && pid != _ownPid)
                        return true;
                }
            }
            finally
            {
                device.Dispose();
            }
        }
        return false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Elapsed -= OnTick;
        _timer.Dispose();
    }
}
