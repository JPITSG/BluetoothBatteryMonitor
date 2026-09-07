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
// Startup: Windows' known battery should fill the connected mouse icon
// without waiting for a live notification, while the other two stay hidden.
Check(!new MonitorDeviceState("Offline").TrySeedBattery(0, 65), "A cached battery must never establish an offline device connection.");
var mouse = new MonitorDeviceState("Mouse");
var mouseSession = mouse.BeginConnection("mouse endpoint");
Check(mouse.ConfirmConnection(mouseSession, true), "Confirm the mouse connection independently of its cached battery.");
Check(mouse.TrySeedBattery(mouseSession, 67), "Use Windows' known battery immediately for the connected mouse.");
Check(mouse.StatusText == "Battery: 67%", "Startup should display the known percentage before movement.");
Check(mouse.LastUpdate == null, "A cached value has no known freshness timestamp.");
var startupVisible = TrayVisibility.SelectVisible(new[] {
    new TrayDevice("Headphones", false, true), new TrayDevice("Keyboard", false, false),
    new TrayDevice(mouse.Name, mouse.IsConnected, false)
});
Check(startupVisible.SetEquals(new[] { "Mouse" }), "Startup must replace the disconnected fallback with the connected mouse.");
var liveTime = new DateTime(2026, 9, 7, 12, 0, 0);
Check(mouse.TryUpdateBattery(mouseSession, 66, liveTime), "A live value should replace the startup cache.");
Check(!mouse.TrySeedBattery(mouseSession, 67) && mouse.BatteryLevel == 66 && mouse.LastUpdate == liveTime,
    "A delayed Windows cache result must not overwrite a fresh notification or its timestamp.");
mouse.Disconnect();
Check(!mouse.TrySeedBattery(mouseSession, 67) && mouse.StatusText == "Disconnected", "A startup cache arriving after disconnect must not revive the icon's battery.");
var reconnected = mouse.BeginConnection("mouse endpoint");
Check(mouse.ConfirmConnection(reconnected, true), "Mouse reconnection should succeed.");
Check(!mouse.TrySeedBattery(mouseSession, 67), "A cache read from the previous session must be rejected after reconnect.");
Check(!mouse.TrySeedBattery(reconnected, 101), "Windows' unknown-battery sentinel must remain unknown.");
Check(!mouse.TrySeedBattery(reconnected, -1), "Invalid cached battery must remain unknown.");
Check(mouse.TrySeedBattery(reconnected, 0), "A confirmed connected device can have a genuine cached zero.");
Check(!mouse.TrySeedBattery(reconnected, 50) && mouse.BatteryLevel == 0, "Zero is a known value, not an uninitialized battery.");
Console.WriteLine($"Passed {checks} checks: tray visibility, connection transitions, stale reads, and battery status.");
