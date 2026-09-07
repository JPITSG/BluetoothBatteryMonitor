using BluetoothBatteryMonitor;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}

// One-device and multi-device policy, for every connection/previous-visibility
// combination up to four devices (including startup and simultaneous losses).
for (int count = 1; count <= 4; count++)
for (int connections = 0; connections < (1 << count); connections++)
for (int previous = 0; previous < (1 << count); previous++)
{
    var devices = Enumerable.Range(0, count).Select(i => new TrayDevice(
        $"Device {i}", (connections & (1 << i)) != 0, (previous & (1 << i)) != 0)).ToArray();
    var visible = TrayVisibility.SelectVisible(devices);
    Check(visible.Count >= 1, "Configuration must remain reachable with configured devices.");
    if (connections != 0)
    {
        foreach (var device in devices)
            Check(visible.Contains(device.Name) == device.Connected, "Only connected devices should be visible when any are connected.");
    }
    else
    {
        Check(visible.Count == 1, "Exactly one fallback should remain when all devices disconnect.");
        if (previous != 0)
            Check(devices.Any(d => d.Visible && visible.Contains(d.Name)), "Keep an existing tray icon when the final connection is lost.");
    }
}
Check(TrayVisibility.SelectVisible(Array.Empty<TrayDevice>()).Count == 0, "No configured device icons when configuration is empty (the host supplies the configuration icon).");

// Follow the last-connected device across a sequence, rather than always
// substituting the first configured device when it disconnects.
var sequence = new[] { new TrayDevice("A", false, false), new TrayDevice("B", true, false) };
var active = TrayVisibility.SelectVisible(sequence);
Check(active.SetEquals(new[] { "B" }), "B should be the sole connected icon.");
active = TrayVisibility.SelectVisible(sequence.Select(d => d with { Connected = false, Visible = active.Contains(d.Name) }).ToArray());
Check(active.SetEquals(new[] { "B" }), "B must stay visible after its disconnect.");
active = TrayVisibility.SelectVisible(sequence.Select(d => d with { Connected = d.Name == "A", Visible = active.Contains(d.Name) }).ToArray());
Check(active.SetEquals(new[] { "A" }), "Hide disconnected B when A connects.");

var state = new MonitorDeviceState("Headphones");
Check(state.StatusText == "Disconnected" && state.BatteryLevel == null, "Startup must not fabricate a battery or connection.");
var attempt = state.BeginConnection("cached endpoint");
Check(!state.ConfirmConnection(attempt, false), "An opened but disconnected endpoint must not be connected.");
Check(!state.TryUpdateBattery(attempt, 0, DateTime.Now), "A cached zero must not establish a connection.");
Check(state.StatusText == "Disconnected", "Cached battery data must not change disconnected status.");
Check(state.ConfirmConnection(attempt, true), "A confirmed transport connection should be accepted.");
Check(state.StatusText == "Connected (battery unknown)", "A new connection has unknown battery, not zero.");
Check(state.TryUpdateBattery(attempt, 0, DateTime.Now), "A genuine zero on a connected device remains valid.");
Check(state.StatusText == "Battery: 0%", "Do not hide a genuine empty battery.");
state.Disconnect();
Check(state.BatteryLevel == null && state.LastUpdate == null && state.StatusText == "Disconnected", "Disconnect clears battery, timestamp and connection status.");
Check(!state.TryUpdateBattery(attempt, 75, DateTime.Now), "A late read after disconnect must be rejected.");
Check(!state.ConfirmConnection(attempt, true), "A stale open completing after a disconnect must be rejected.");
var next = state.BeginConnection("new endpoint");
Check(state.ConfirmConnection(next, true), "Reconnection should work.");
Check(!state.TryUpdateBattery(attempt, 0, DateTime.Now), "A previous connection's read must not overwrite a new session.");
Check(state.BatteryLevel == null, "Reconnection must not inherit old battery readings.");
Check(state.TryUpdateBattery(next, 88, DateTime.Now), "Accept current connection data.");
Check(!state.TryUpdateBattery(next, 255, DateTime.Now) && state.BatteryLevel == 88, "Invalid battery data must not overwrite the valid reading.");
Console.WriteLine($"Passed {checks} checks: tray visibility, connection transitions, stale reads, and battery status.");
