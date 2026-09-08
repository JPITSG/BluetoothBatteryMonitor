using BluetoothBatteryMonitor;

internal static class UpdateLaunchArgumentsTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (bool selected in new[] { false, true })
        {
            const string target = @"C:\Program Files\Bluetooth Battery Monitor\app.exe";
            const string staged = @"C:\Users\Test User\AppData\Local\Temp\update.exe";
            const string helper = @"C:\Users\Test User\AppData\Local\Temp\BluetoothBatteryMonitor-helper-test.exe";
            var apply = UpdateLaunchArguments.Apply(123, target, staged, selected);
            check(UpdateLaunchArguments.TryReadApply(apply, out int pid, out bool helperChoice) && pid == 123 && helperChoice == selected,
                "The confirmation choice reaches the updater helper.");
            check(apply[2] == target && apply[3] == staged, "Paths with spaces remain separate process arguments.");
            var completed = UpdateLaunchArguments.Completed(helper, helperChoice);
            check(UpdateLaunchArguments.TryReadCompleted(completed, out string cleanup, out bool startupChoice) &&
                cleanup == helper && startupChoice == selected, "The successful restart preserves the choice and helper cleanup path.");
        }
        check(UpdateLaunchArguments.TryReadCompleted(new[] { "--update-completed", "helper.exe" }, out _, out bool legacy) && !legacy,
            "A completion without an explicit choice leaves settings closed.");
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--reopen-settings" }, new[] { "/configure" },
            new[] { "--update-completed" }, new[] { "--update-completed", "helper.exe", "unexpected" },
            new[] { "--update-completed", "helper.exe", "--reopen-settings", "extra" } })
        {
            check(!UpdateLaunchArguments.TryReadCompleted(args, out _, out bool reopen) && !reopen,
                "Unrelated, rollback, and malformed launches do not consume a reopen choice.");
        }
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--apply-update", "0", "target", "staged" },
            new[] { "--apply-update", "bad", "target", "staged" }, new[] { "--apply-update", "1", "target", "staged", "unexpected" } })
            check(!UpdateLaunchArguments.TryReadApply(args, out _, out bool reopen) && !reopen, "Invalid helper arguments cannot enable reopening.");
        var retry = UpdateLaunchArguments.Apply(123, "target", "staged", false);
        check(UpdateLaunchArguments.TryReadApply(retry, out _, out bool retryChoice) && !retryChoice,
            "A later unchecked attempt does not retain an earlier checked choice.");
    }
}
