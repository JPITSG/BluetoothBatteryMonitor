using System.Drawing;
using BluetoothBatteryMonitor;

internal static class TrayIconRefreshTests
{
    public static void Run(Action<bool, string> check)
    {
        var local = new Size(16, 16);
        var remote = new Size(32, 32);
        var refresh = new TrayIconRefreshState();
        check(refresh.ShouldRefresh(local, 0), "Startup establishes icons at the taskbar's current size.");
        check(!refresh.ShouldRefresh(local, 1000), "An unchanged taskbar does not continuously rebuild icons.");

        refresh.Request(2000); // RDP connects before the remote DPI is applied.
        check(refresh.ShouldRefresh(local, 2000), "Session changes refresh icons even before DPI changes.");
        check(refresh.ShouldRefresh(remote, 2500), "A delayed RDP DPI change is picked up between scheduled retries.");
        check(!refresh.ShouldRefresh(remote, 2501), "A DPI change does not cause duplicate refreshes on every tick.");
        check(refresh.ShouldRefresh(remote, 3000), "A follow-up refresh replaces Explorer's transitional image.");

        refresh.Request(4000); // Returning to the local session can take several seconds.
        check(refresh.ShouldRefresh(remote, 4000), "Local reconnect starts recovery while the remote taskbar still exists.");
        check(!refresh.ShouldRefresh(null, 7000), "An absent Explorer keeps the existing icons and pending recovery.");
        check(!refresh.ShouldRefresh(Size.Empty, 7000), "An invalid taskbar metric cannot replace icons with an empty image.");
        check(refresh.ShouldRefresh(local, 7500), "Restoring local DPI after Explorer returns refreshes the icon.");
        check(!refresh.ShouldRefresh(local, 7501), "Missed retries are coalesced instead of causing a burst of redraws.");
        check(refresh.ShouldRefresh(local, 9000), "Recovery continues after the first successful local redraw.");
        check(refresh.ShouldRefresh(local, 14000), "Explorer can settle ten seconds after reconnect.");
        check(refresh.ShouldRefresh(local, 24000), "Explorer can settle twenty seconds after reconnect.");
        check(refresh.ShouldRefresh(local, 34000), "Recovery includes a final redraw thirty seconds after reconnect.");
        check(!refresh.ShouldRefresh(local, 35000) && !refresh.ShouldRefresh(local, 90000),
            "Forced redraws stop after the recovery window.");
        check(refresh.ShouldRefresh(new Size(20, 20), 91000), "A later 125% scaling change is detected without a session notification.");

        refresh.Request(92000); // Unlock follows an earlier console-connect event.
        check(refresh.ShouldRefresh(new Size(20, 20), 92000), "Unlock starts a fresh recovery window even with unchanged DPI.");
        refresh.Request(92500);
        check(refresh.ShouldRefresh(new Size(20, 20), 92500), "A newer event is not dropped while recovery is pending.");
        check(refresh.ShouldRefresh(new Size(20, 20), 122500), "Recovery extends to thirty seconds after the latest event.");
        check(!refresh.ShouldRefresh(new Size(20, 20), 122501), "A late tick performs only one final redraw.");

        refresh.Request(130000);
        check(!refresh.ShouldRefresh(null, 161000), "Explorer can be unavailable longer than the recovery window.");
        check(refresh.ShouldRefresh(new Size(20, 20), 162000), "An overdue refresh is preserved until Explorer is available.");

        // Exercise the shell boundary: unchanged battery readings still submit
        // fresh HICONs, while GUID, visibility, and registration are preserved.
        var identity = TrayIconIdentity.ForDevice("RDP test mouse");
        var commands = new List<(TrayCommand Command, TrayIconData Data)>();
        var registration = new TrayIconRegistration(identity, (command, data) =>
        {
            commands.Add((command, data));
            return true;
        });
        var window = new IntPtr(10);
        registration.Update(window, new IntPtr(100), "Battery: 60%", true);
        registration.Update(window, new IntPtr(101), "Battery: 60%", true); // Remote artwork
        registration.Update(window, new IntPtr(102), "Battery: 60%", true); // Local artwork
        check(commands[^1].Command == TrayCommand.Modify && commands[^1].Data.Icon == new IntPtr(102),
            "The new local icon is submitted even if battery level and tooltip are unchanged.");
        check(commands.Count(c => c.Command == TrayCommand.Add) == 1 && commands.All(c => c.Command != TrayCommand.Delete),
            "Routine artwork changes preserve the existing shell registration.");
        check(commands.All(c => c.Data.Identity == identity), "RDP recovery preserves the device's stable tray GUID.");
        registration.Update(window, new IntPtr(103), "Disconnected", false);
        check(commands[^1].Data.Icon == new IntPtr(103) && commands[^1].Data.State == 1,
            "Refreshing an offline icon does not make it visible.");

        // Explorer can announce a rebuilt taskbar while the shell still holds
        // this icon. NIM_ADD can then never succeed, and until that was handled
        // the icon kept the artwork published for the previous session's DPI,
        // which the shell rescaled into the local tray and drew blurred.
        var held = new List<(TrayCommand Command, TrayIconData Data)>();
        bool shellHoldsIcon = false;
        var stale = new TrayIconRegistration(identity, (command, data) =>
        {
            held.Add((command, data));
            return command switch
            {
                TrayCommand.Add => !shellHoldsIcon,
                TrayCommand.Modify => shellHoldsIcon,
                _ => true
            };
        });
        check(stale.Update(window, new IntPtr(300), "Battery: 60%", true), "A first registration adds the icon.");
        shellHoldsIcon = true;
        stale.ExplorerRestarted();
        int at = held.Count;
        check(stale.Update(window, new IntPtr(301), "Battery: 60%", true),
            "A rebuilt taskbar that kept this icon must not block every later update.");
        check(held.Skip(at).Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Add, TrayCommand.Modify, TrayCommand.SetVersion }),
            "A rejected add adopts the registration the shell still holds.");
        check(held[^2].Data.Icon == new IntPtr(301) && held[^2].Data.State == 0,
            "Adopting a held registration publishes the current artwork at the local size.");
        check(stale.Version4, "An adopted registration restores modern mouse and keyboard callbacks.");
        at = held.Count;
        check(stale.Update(window, new IntPtr(302), "Battery: 55%", true) &&
            held.Skip(at).Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Modify }),
            "Later refreshes reuse the adopted registration instead of adding again.");
        check(held.All(c => c.Command != TrayCommand.Delete),
            "Recovery never deletes an icon, which would drop it from the user's tray order.");

        // The opposite stale state: the app believes it is still registered but
        // the shell dropped the icon without broadcasting a taskbar restart.
        at = held.Count;
        shellHoldsIcon = false;
        check(stale.Update(window, new IntPtr(303), "Battery: 55%", true),
            "An icon the shell silently dropped is registered again.");
        check(held.Skip(at).Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Modify, TrayCommand.Add, TrayCommand.SetVersion }),
            "A rejected modify falls back to adding the icon back.");
        check(held[^2].Data.Icon == new IntPtr(303), "Re-adding a dropped icon publishes the current artwork.");

        // Neither command can succeed while Explorer is mid-transition.
        var offline = new List<TrayCommand>();
        bool shellReady = false;
        var waiting = new TrayIconRegistration(identity, (command, _) =>
        {
            offline.Add(command);
            return shellReady;
        });
        check(!waiting.Update(window, new IntPtr(400), "Battery: 60%", true) && !waiting.Version4,
            "An unreachable shell keeps the update retryable and claims no registration.");
        check(offline.SequenceEqual(new[] { TrayCommand.Add, TrayCommand.Modify }),
            "Both shell commands are attempted before reporting a failure.");
        shellReady = true;
        offline.Clear();
        check(waiting.Update(window, new IntPtr(401), "Battery: 60%", true),
            "Recovery succeeds once Explorer is ready again.");
        check(offline.SequenceEqual(new[] { TrayCommand.Add, TrayCommand.SetVersion }),
            "The retry publishes the latest artwork through a fresh registration.");

        // Icons that have never been shown stay out of the tray entirely.
        var hidden = new List<TrayCommand>();
        var deferred = new TrayIconRegistration(identity, (command, _) => { hidden.Add(command); return true; });
        check(deferred.Update(window, new IntPtr(500), "Disconnected", false) && hidden.Count == 0,
            "A device that has never been connected is never registered.");
        check(deferred.Update(window, IntPtr.Zero, "Battery: 60%", true) && hidden.Count == 0,
            "An icon is never registered before its artwork is available.");

        // Restart and shutdown keep addressing the same published identity.
        var lifecycle = new List<(TrayCommand Command, TrayIconData Data)>();
        var restarted = new TrayIconRegistration(identity, (command, data) => { lifecycle.Add((command, data)); return true; });
        restarted.Update(window, new IntPtr(600), "Battery: 60%", true);
        restarted.ExplorerRestarted();
        at = lifecycle.Count;
        restarted.Update(new IntPtr(11), new IntPtr(601), "Battery: 60%", true);
        check(lifecycle.Skip(at).Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Add, TrayCommand.SetVersion }),
            "A genuine Explorer restart registers the icon on the new taskbar.");
        restarted.Remove(new IntPtr(11));
        check(lifecycle[^1].Command == TrayCommand.Delete, "Application shutdown removes the shell icon.");
        at = lifecycle.Count;
        check(restarted.Update(new IntPtr(11), new IntPtr(602), "Disconnected", false) && lifecycle.Count == at,
            "A removed icon is not resurrected while it stays hidden.");
        check(lifecycle.All(c => c.Data.Identity == identity && c.Data.Flags.HasFlag(TrayFlags.Guid)),
            "Every shell command addresses the same stable device GUID.");
    }
}
