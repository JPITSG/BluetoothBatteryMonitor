using BluetoothBatteryMonitor;

internal static class BatteryRefreshTests
{
    public static void Run(Action<bool, string> check)
    {
        var mouse = new MonitorDeviceState("Mouse");
        long generation = mouse.BeginConnection("mouse endpoint");
        mouse.ConfirmConnection(generation, true);
        var refresh = new BatteryRefreshState();
        check(refresh.NeedsRefresh(mouse.BatteryLevel, true), "A discovered characteristic with unknown battery still requires a read.");

        var initial = refresh.TryBegin(generation)!;
        check(initial != null && refresh.TryBegin(generation) == null, "Overlapping watcher/timer callbacks must not queue duplicate battery operations.");
        refresh.Complete(initial!, success: false);
        check(refresh.NeedsRefresh(mouse.BatteryLevel, true), "A failed initial read remains retryable even though discovery succeeded.");
        var retry = refresh.TryBegin(generation)!;
        check(retry != null, "An unknown battery gets another attempt after a read failure.");
        check(mouse.TrySeedBattery(generation, 67) && mouse.LastUpdate == null, "A later Windows cache value fills the unknown battery without claiming a fresh device timestamp.");
        refresh.Complete(retry!, success: false);
        check(refresh.NeedsRefresh(mouse.BatteryLevel, true), "A successful cache read alone does not suppress retries for a failed live read or subscription.");

        var live = refresh.TryBegin(generation)!;
        check(mouse.TryUpdateBattery(generation, 66, DateTime.Now), "The eventual live reading replaces the recovery cache value.");
        refresh.Complete(live, success: true);
        check(!refresh.NeedsRefresh(mouse.BatteryLevel, true), "A recovered, subscribed device stops unnecessary LE reads.");
        check(refresh.NeedsRefresh(null, true), "Losing the percentage requires a read even with an existing characteristic and previous success.");
        check(refresh.NeedsRefresh(mouse.BatteryLevel, false), "A missing characteristic requires discovery even with a known battery.");

        refresh.Request();
        check(refresh.NeedsRefresh(mouse.BatteryLevel, true) && mouse.BatteryLevel == 66 && mouse.IsConnected, "Wake requests new battery evidence without blanking the icon or inventing a disconnection.");
        var beforeUnlock = refresh.TryBegin(generation)!;
        refresh.Request(); // Unlock arrives while a resume-triggered operation is pending.
        check(refresh.TryBegin(generation) == null, "A second wake/unlock request does not overlap the same connection's IO.");
        refresh.Complete(beforeUnlock, success: true);
        check(refresh.NeedsRefresh(mouse.BatteryLevel, true), "A pre-unlock operation completing successfully cannot swallow the newer recovery request.");
        var afterUnlock = refresh.TryBegin(generation)!;
        refresh.Complete(beforeUnlock, success: false);
        check(refresh.TryBegin(generation) == null, "A stale completion cannot unlock a newer in-flight attempt.");
        refresh.Complete(afterUnlock, success: true);
        check(!refresh.NeedsRefresh(mouse.BatteryLevel, true), "The newest successful wake attempt completes recovery.");

        refresh.Request();
        var beforeDisconnect = refresh.TryBegin(generation)!;
        mouse.Disconnect();
        refresh.Reset();
        long reconnected = mouse.BeginConnection("mouse endpoint");
        mouse.ConfirmConnection(reconnected, true);
        var current = refresh.TryBegin(reconnected)!;
        refresh.Complete(beforeDisconnect, success: true);
        check(refresh.NeedsRefresh(mouse.BatteryLevel, true) && refresh.TryBegin(reconnected) == null,
            "A read from before disconnect cannot complete or unlock the new connection's recovery.");
        check(!mouse.TrySeedBattery(generation, 70) && !mouse.TryUpdateBattery(generation, 70, DateTime.Now),
            "Late cached/live battery results from an old connection cannot repopulate the new connection.");
        mouse.TrySeedBattery(reconnected, 70);
        refresh.Complete(current, success: true);
        check(!refresh.NeedsRefresh(mouse.BatteryLevel, true), "The reconnected session can recover independently of an abandoned old attempt.");

        // A slow headset cannot monopolize the mouse's battery-operation gate.
        var headsetRefresh = new BatteryRefreshState();
        var headset = headsetRefresh.TryBegin(1);
        refresh.Request();
        var mouseAttempt = refresh.TryBegin(reconnected);
        check(headset != null && mouseAttempt != null, "Different devices can recover concurrently.");
        refresh.Complete(mouseAttempt!, success: true);
        check(headsetRefresh.TryBegin(1) == null && !refresh.NeedsRefresh(70, true), "Mouse recovery can finish while the headset is still waiting.");

        for (int i = 0; i < 3; i++)
        {
            refresh.Request();
            var failed = refresh.TryBegin(reconnected)!;
            refresh.Complete(failed, success: false);
            check(refresh.NeedsRefresh(70, true), "Repeated transient failures remain retryable without losing the displayed battery.");
        }
    }
}
