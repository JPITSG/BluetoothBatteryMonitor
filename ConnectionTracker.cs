using System;
using System.Collections.Generic;
using System.Linq;

namespace BluetoothBatteryMonitor;

// Turns each monitored device's connection state, as configuration shows it
// and sampled every second, into the connection changes kept in its history.
internal sealed class ConnectionTracker
{
    // A state must last this long to be recorded. The app briefly drops and
    // re-establishes connections itself when it reloads devices or restarts
    // its watchers, and blips this short would not show on a graph anyway.
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    // After startup, wake, unlock, or a configuration change, devices take a
    // few seconds to be found again. Nothing is recorded until then; states
    // that began meanwhile are still dated from when they began.
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);
    // A longer pause between samples means the process was suspended, so
    // nothing was monitored from the previous sample until now.
    public static readonly TimeSpan SuspendGap = TimeSpan.FromSeconds(30);

    private readonly Action<string, string, DateTimeOffset> _record;
    // Power and session notifications can arrive off the UI thread.
    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastSample;
    private DateTimeOffset _settleUntil;

    private sealed class Device
    {
        public bool Connected;
        public DateTimeOffset Since;
        public string? Recorded;
    }

    public ConnectionTracker(Action<string, string, DateTimeOffset> record) => _record = record;

    public void Settle(DateTimeOffset now)
    {
        lock (_gate) SettleLocked(now);
    }

    private void SettleLocked(DateTimeOffset now)
    {
        if (now + SettleTime > _settleUntil) _settleUntil = now + SettleTime;
    }

    public void Sample(DateTimeOffset now, IEnumerable<(string Name, bool Connected)> devices)
    {
        var observed = devices.ToArray();
        lock (_gate) SampleLocked(now, observed);
    }

    private void SampleLocked(DateTimeOffset now, (string Name, bool Connected)[] devices)
    {
        if (_lastSample is { } previous && now - previous > SuspendGap)
        {
            StopLocked(previous);
            SettleLocked(now);
        }
        _lastSample = now;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, connected) in devices)
        {
            if (!present.Add(name)) continue;
            if (!_devices.TryGetValue(name, out var device))
                _devices[name] = device = new Device { Connected = connected, Since = now };
            else if (device.Connected != connected)
            {
                device.Connected = connected;
                device.Since = now;
            }
            string state = connected ? ConnectionState.Connected : ConnectionState.Disconnected;
            if (device.Recorded != state && now - device.Since >= Grace && now >= _settleUntil)
            {
                _record(name, state, device.Since);
                device.Recorded = state;
            }
        }
        // A device removed from monitoring is no longer observed.
        foreach (var name in _devices.Keys.Where(name => !present.Contains(name)).ToArray())
        {
            _record(name, ConnectionState.Unmonitored, now);
            _devices.Remove(name);
        }
    }

    // The app is exiting, the session is ending, or the PC is going to sleep.
    // Samples taken before it actually sleeps are only recorded after settling.
    public void Stop(DateTimeOffset at)
    {
        lock (_gate)
        {
            StopLocked(at);
            SettleLocked(at);
        }
    }

    private void StopLocked(DateTimeOffset at)
    {
        foreach (var name in _devices.Keys) _record(name, ConnectionState.Unmonitored, at);
        _devices.Clear();
    }
}
