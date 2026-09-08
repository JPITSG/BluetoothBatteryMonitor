using BluetoothBatteryMonitor;

internal static class EndpointRecoveryTests
{
    public static void Run(Action<bool, string> check)
    {
        // Captured failure: two same-name AEPs claim connected, but the stale
        // adapter's endpoint cannot be opened. Its flag must not pin selection.
        foreach (bool invalidFirst in new[] { true, false })
        {
            var mouse = new MonitorDeviceState("MX Anywhere 3S");
            var endpoints = invalidFirst ? new[] { "invalid", "local" } : new[] { "local", "invalid" };
            foreach (string endpoint in endpoints)
            {
                if (mouse.IsConnected) break;
                long generation = mouse.BeginConnection(endpoint);
                check(mouse.ConfirmConnection(generation, ConnectionEvidence.IsConnected(true, true, false)),
                    "Windows' provisional connection is visible while native opening is pending.");
                if (endpoint == "invalid")
                {
                    mouse.TrySeedBattery(generation, 81);
                    check(mouse.TryRejectEndpoint(generation), "Reject the endpoint Windows cannot open.");
                    check(!mouse.IsConnected && mouse.BatteryLevel == null && mouse.LastUpdate == null,
                        "A rejected endpoint cannot retain its provisional connection or cached percentage.");
                    check(!mouse.ConfirmConnection(generation, true),
                        "The invalid endpoint's persistent Windows-connected flag cannot reconfirm it.");
                    check(!mouse.TrySeedBattery(generation, 81) && !mouse.TryUpdateBattery(generation, 81, DateTime.Now),
                        "Late battery data cannot revive a rejected endpoint.");
                }
                else
                {
                    mouse.TryUpdateBattery(generation, 60, DateTime.Now);
                }
            }
            check(mouse.DeviceId == "local" && mouse.DisplayStatus == new DeviceStatus("MX Anywhere 3S", true, 60),
                "The usable same-name endpoint wins in either discovery order.");
        }

        var recovered = new MonitorDeviceState("Mouse");
        long old = recovered.BeginConnection("temporarily unavailable endpoint");
        recovered.ConfirmConnection(old, true);
        recovered.TryRejectEndpoint(old);
        long retry = recovered.BeginConnection("temporarily unavailable endpoint");
        check(!recovered.EndpointRejected && recovered.ConfirmConnection(retry, true),
            "A later attempt can recover the same endpoint; rejection is not a permanent blacklist.");
        var timestamp = DateTime.Now;
        recovered.TryUpdateBattery(retry, 60, timestamp);
        check(!recovered.TryRejectEndpoint(old) && recovered.IsConnected && recovered.BatteryLevel == 60 &&
            recovered.LastUpdate == timestamp && !recovered.EndpointRejected,
            "An old native open failing late cannot invalidate a newer connection or its battery.");
        recovered.Disconnect();
        check(!recovered.TryRejectEndpoint(retry) && !recovered.EndpointRejected,
            "A rejection from before disconnect cannot poison the next connection.");
    }
}
