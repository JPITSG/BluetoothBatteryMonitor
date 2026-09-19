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

        registration.RequestRecreation();
        int before = commands.Count;
        check(registration.Update(window, new IntPtr(104), "Disconnected", false), "Hidden icon recovery remains successful.");
        check(commands.Skip(before).All(c => c.Command == TrayCommand.Modify) && commands[^1].Data.State == 1,
            "Hidden icons defer cache recreation until shown, without flashing in the tray.");
        before = commands.Count;
        check(registration.Update(window, new IntPtr(105), "Battery: 60%", true), "A recovered device can show its new artwork.");
        check(commands.Skip(before).Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Delete, TrayCommand.Add, TrayCommand.SetVersion }),
            "Showing an icon after RDP recovery discards Explorer's cached image before registering the replacement.");
        check(commands[^2].Data.Icon == new IntPtr(105) && commands[^2].Data.Tip == "Battery: 60%" && commands[^2].Data.State == 0,
            "The replacement uses the latest HICON, battery tooltip, and visible state.");
        check(registration.Version4, "Recreated icons retain modern mouse and keyboard callbacks.");

        registration.RequestRecreation();
        registration.RequestRecreation();
        before = commands.Count;
        registration.Update(window, new IntPtr(106), "Battery: 60%", true);
        check(commands.Skip(before).Select(c => c.Command).SequenceEqual(new[] { TrayCommand.Delete, TrayCommand.Add, TrayCommand.SetVersion }),
            "Same-DPI recovery clears the shell cache, and duplicate requests coalesce into one recreation.");
        check(commands.All(c => c.Data.Identity == identity && c.Data.Flags.HasFlag(TrayFlags.Guid)),
            "Deleting and re-adding a degraded icon must keep the published device GUID.");
        before = commands.Count;
        registration.Update(window, new IntPtr(106), "Battery: 60% Updated: 1s", true);
        check(commands.Skip(before).All(c => c.Command == TrayCommand.Modify), "Routine tooltip updates do not keep recreating icons.");

        bool failDelete = false, failAdd = false;
        var retries = new List<TrayCommand>();
        var recovering = new TrayIconRegistration(identity, (command, _) =>
        {
            retries.Add(command);
            return !(command == TrayCommand.Delete && failDelete || command == TrayCommand.Add && failAdd);
        });
        recovering.Update(window, new IntPtr(200), "Mouse", true);
        recovering.RequestRecreation();
        failDelete = true;
        before = retries.Count;
        check(!recovering.Update(window, new IntPtr(201), "Mouse", true), "A failed recovery delete requests another shell attempt.");
        check(retries.Skip(before).SequenceEqual(new[] { TrayCommand.Delete }) && recovering.Version4,
            "A failed delete must not add a duplicate or discard the current callback state.");
        failDelete = false;
        failAdd = true;
        before = retries.Count;
        check(!recovering.Update(window, new IntPtr(202), "Mouse", true) && !recovering.Version4,
            "A failed replacement add remains retryable after the old registration is removed.");
        check(retries.Skip(before).SequenceEqual(new[] { TrayCommand.Delete, TrayCommand.Add }),
            "A failed replacement does not claim success or set its callback version.");
        failAdd = false;
        before = retries.Count;
        check(recovering.Update(window, new IntPtr(203), "Mouse", true), "Recovery retries the replacement when Explorer becomes ready.");
        check(retries.Skip(before).SequenceEqual(new[] { TrayCommand.Add, TrayCommand.SetVersion }),
            "Retrying a failed add does not delete an already-removed registration again.");

        recovering.RequestRecreation();
        recovering.ExplorerRestarted();
        before = retries.Count;
        recovering.Update(window, new IntPtr(204), "Mouse", true);
        check(retries.Skip(before).SequenceEqual(new[] { TrayCommand.Add, TrayCommand.SetVersion }),
            "An Explorer restart supersedes pending recreation without deleting from the new shell.");
        recovering.Remove(window);
        recovering.RequestRecreation();
        before = retries.Count;
        check(recovering.Update(window, new IntPtr(205), "Disconnected", false) && retries.Count == before,
            "Recovery never registers an initially hidden icon.");
    }
}
