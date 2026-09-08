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
Check(state.DisplayStatus == new DeviceStatus("Headphones", false, null), "Configuration starts offline without a fabricated battery percentage.");
var attempt = state.BeginConnection("cached endpoint");
Check(!state.ConfirmConnection(attempt, false), "An opened but disconnected endpoint must not be connected.");
Check(!state.TryUpdateBattery(attempt, 0, DateTime.Now), "A cached zero must not establish a connection.");
Check(state.StatusText == "Disconnected", "Cached battery data must not change disconnected status.");
Check(state.ConfirmConnection(attempt, true), "A confirmed transport connection should be accepted.");
Check(state.StatusText == "Connected (battery unknown)", "A new connection has unknown battery, not zero.");
Check(state.DisplayStatus == new DeviceStatus("Headphones", true, null), "An online device with no reading keeps battery unknown in configuration.");
Check(state.TryUpdateBattery(attempt, 0, DateTime.Now), "A genuine zero on a connected device remains valid.");
Check(state.StatusText == "Disconnected" && !state.IsConnectedForDisplay, "Zero battery must use disconnected display rules.");
Check(state.DisplayStatus == new DeviceStatus("Headphones", false, null), "Zero battery must publish offline with no percentage to configuration.");
state.Disconnect();
Check(state.BatteryLevel == null && state.LastUpdate == null && state.StatusText == "Disconnected", "Disconnect clears battery, timestamp and connection status.");
Check(state.DisplayStatus == new DeviceStatus("Headphones", false, null), "A disconnect removes the configuration percentage.");
Check(!state.TryUpdateBattery(attempt, 75, DateTime.Now), "A late read after disconnect must be rejected.");
Check(!state.ConfirmConnection(attempt, true), "A stale open completing after a disconnect must be rejected.");
var next = state.BeginConnection("new endpoint");
Check(state.ConfirmConnection(next, true), "Reconnection should work.");
Check(!state.TryUpdateBattery(attempt, 0, DateTime.Now), "A previous connection's read must not overwrite a new session.");
Check(state.BatteryLevel == null, "Reconnection must not inherit old battery readings.");
Check(state.TryUpdateBattery(next, 88, DateTime.Now), "Accept current connection data.");
Check(!state.TryUpdateBattery(next, 255, DateTime.Now) && state.BatteryLevel == 88, "Invalid battery data must not overwrite the valid reading.");
Check(state.DisplayStatus == new DeviceStatus("Headphones", true, 88), "Current battery readings appear in the configuration snapshot.");
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
// The system endpoint can know an in-use paired mouse is connected before
// the separate Bluetooth object reports it. That mismatch must not hide it.
Check(ConnectionEvidence.IsConnected(true, true, false), "Windows-connected paired mouse must survive a lagging native status.");
Check(ConnectionEvidence.IsConnected(true, true, true), "Matching positive connection signals must be accepted.");
Check(!ConnectionEvidence.IsConnected(true, false, true), "An explicit Windows disconnect must override a stale native connection.");
Check(!ConnectionEvidence.IsConnected(true, false, false), "Matching negative signals remain disconnected.");
Check(!ConnectionEvidence.IsConnected(false, true, false), "An unpaired phantom endpoint is not a confirmed connection.");
Check(!ConnectionEvidence.IsConnected(true, null, false), "Pairing alone does not imply a connection.");
Check(ConnectionEvidence.IsConnected(true, null, true), "Native status is a fallback when Windows omits the endpoint property.");
var systemMouse = new MonitorDeviceState("System mouse");
var systemSession = systemMouse.BeginConnection("paired mouse endpoint");
Check(systemMouse.ConfirmConnection(systemSession, ConnectionEvidence.IsConnected(true, true, false)), "Confirm the system's connection before waiting for the Bluetooth object.");
Check(systemMouse.TrySeedBattery(systemSession, 70), "Display Windows' battery for the system-connected mouse immediately.");
var systemVisible = TrayVisibility.SelectVisible(new[] {
    new TrayDevice("Headphones", false, true), new TrayDevice("Keyboard", false, false),
    new TrayDevice(systemMouse.Name, systemMouse.IsConnected, false)
});
Check(systemVisible.SetEquals(new[] { systemMouse.Name }), "The Windows-connected mouse replaces the disconnected fallback icon.");
if (!ConnectionEvidence.IsConnected(true, false, true)) systemMouse.Disconnect();
Check(systemMouse.StatusText == "Disconnected" && systemMouse.BatteryLevel == null, "A subsequent Windows disconnect clears the seeded battery.");
Check(!systemMouse.TrySeedBattery(systemSession, 70), "A cache result cannot override a Windows disconnect.");
// Zero-percent readings behave as disconnected for status and visibility,
// while retaining the subscription so a later positive value can recover.
var empty = new MonitorDeviceState("Empty");
var emptySession = empty.BeginConnection("empty endpoint");
empty.ConfirmConnection(emptySession, true);
Check(empty.TryUpdateBattery(emptySession, 0, liveTime), "Accept zero so its disconnected display policy can be applied.");
Check(empty.IsConnected && !empty.IsConnectedForDisplay && empty.StatusText == "Disconnected", "Keep monitoring while displaying zero as disconnected.");
var singleEmpty = TrayVisibility.SelectVisible(new[] { new TrayDevice(empty.Name, empty.IsConnectedForDisplay, true) });
Check(singleEmpty.SetEquals(new[] { empty.Name }), "A single zero-battery device must retain configuration access.");
var withCharged = TrayVisibility.SelectVisible(new[] {
    new TrayDevice(empty.Name, empty.IsConnectedForDisplay, true), new TrayDevice("Charged", true, false)
});
Check(withCharged.SetEquals(new[] { "Charged" }), "Hide a zero-battery device while another device is available.");
var allEmpty = TrayVisibility.SelectVisible(new[] {
    new TrayDevice(empty.Name, empty.IsConnectedForDisplay, true), new TrayDevice("Offline", false, false)
});
Check(allEmpty.SetEquals(new[] { empty.Name }), "Do not hide the final icon when every device is zero or disconnected.");
Check(empty.TryUpdateBattery(emptySession, 20, liveTime.AddSeconds(1)) && empty.IsConnectedForDisplay,
    "A later positive battery notification must restore normal visibility without a physical reconnect.");

// Identities do not depend on process-local icon IDs, discovery order, casing,
// executable version or which other devices are currently connected.
var mouseId = TrayIconIdentity.ForDevice("Logitech MX Master 3");
Check(mouseId == TrayIconIdentity.ForDevice("LOGITECH MX MASTER 3"), "Case changes must not reset tray preferences.");
Check(mouseId != TrayIconIdentity.ForDevice("Headphones"), "Different devices need distinct shell identities.");
Check(TrayIconIdentity.Configuration != TrayIconIdentity.ForDevice("configuration"), "The fallback icon has its own identity namespace.");
Check(mouseId == new Guid("bc5a00ac-d9c6-7eef-d815-04decc5df3b0"), "The published tray identity must remain stable across releases.");
Check(System.Runtime.InteropServices.Marshal.SizeOf<TrayIconData>() == (IntPtr.Size == 8 ? 976 : 956), "NOTIFYICONDATAW must use the Windows ABI layout.");

var calls = new List<(TrayCommand Command, TrayIconData Data)>();
var registration = new TrayIconRegistration(mouseId, (command, data) => { calls.Add((command, data)); return true; });
var window = new IntPtr(10);
var iconHandle = new IntPtr(20);
Check(registration.Update(window, iconHandle, "Mouse", false) && calls.Count == 0, "An initially disconnected icon must not be added to Explorer.");
Check(registration.Update(window, iconHandle, "Mouse", true), "Initial registration should succeed.");
Check(calls.Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Add, TrayCommand.SetVersion }), "Add and set callback version on first display.");
Check(registration.Version4 && calls[1].Data.Version == 4, "Use modern keyboard and tooltip callbacks.");
registration.Update(window, iconHandle, "Disconnected", false);
Check(calls[^1].Command == TrayCommand.Modify && calls[^1].Data.State == 1 && calls[^1].Data.StateMask == 1,
    "Disconnect hides the existing shell registration rather than deleting it.");
registration.Update(window, iconHandle, "Battery: 60%", true);
Check(calls[^1].Command == TrayCommand.Modify && calls[^1].Data.State == 0, "Reconnect unhides the same registration.");
Check(calls.Count(c => c.Command == TrayCommand.Add) == 1 && calls.All(c => c.Command != TrayCommand.Delete), "Routine connection changes must not recreate the icon.");
registration.ExplorerRestarted();
registration.Update(new IntPtr(30), iconHandle, "Battery: 60%", true);
Check(calls[^2].Command == TrayCommand.Add && calls[^1].Command == TrayCommand.SetVersion, "Restore the icon after Explorer restarts.");
registration.ReturnFocus(new IntPtr(30));
registration.Remove(new IntPtr(30));
Check(calls[^1].Command == TrayCommand.Delete, "Application shutdown removes the shell icon.");
Check(calls.All(c => c.Data.Identity == mouseId && c.Data.Flags.HasFlag(TrayFlags.Guid)), "Every shell operation must address the same stable GUID, even after a window changes.");

var available = false;
var retryCalls = new List<TrayCommand>();
var retryRegistration = new TrayIconRegistration(mouseId, (command, data) => { retryCalls.Add(command); return available; });
Check(!retryRegistration.Update(window, iconHandle, "Mouse", true), "Shell registration failure must request a retry.");
available = true;
Check(retryRegistration.Update(window, iconHandle, "Mouse", true), "Retry when Explorer becomes available.");
Check(retryCalls.SequenceEqual(new[] { TrayCommand.Add, TrayCommand.Add, TrayCommand.SetVersion }), "A failed add must not be mistaken for a registered icon.");
await UpdateDownloadTests.RunAsync(Check);
UpdateLaunchArgumentsTests.Run(Check);
await BatteryHistoryTests.RunAsync(Check);
Console.WriteLine($"Passed {checks} checks: tray visibility, connection transitions, stale reads, battery status, persistent tray registration, update downloads, and battery history.");
