using System;

namespace BluetoothBatteryMonitor;

// The choice belongs only to this helper/restart handoff; it is never persisted.
internal static class UpdateLaunchArguments
{
    private const string ReopenSettings = "--reopen-settings";

    internal static string[] Apply(int pid, string target, string staged, bool reopenSettings) => reopenSettings
        ? new[] { "--apply-update", pid.ToString(), target, staged, ReopenSettings }
        : new[] { "--apply-update", pid.ToString(), target, staged };

    internal static bool TryReadApply(string[] args, out int pid, out bool reopenSettings)
    {
        pid = 0;
        reopenSettings = false;
        if ((args.Length != 4 && args.Length != 5) || args[0] != "--apply-update" ||
            (args.Length == 5 && args[4] != ReopenSettings) || !int.TryParse(args[1], out pid) || pid <= 0) return false;
        reopenSettings = args.Length == 5;
        return true;
    }

    internal static string[] Completed(string helper, bool reopenSettings) => reopenSettings
        ? new[] { "--update-completed", helper, ReopenSettings }
        : new[] { "--update-completed", helper };

    internal static bool TryReadCompleted(string[] args, out string helper, out bool reopenSettings)
    {
        helper = "";
        reopenSettings = false;
        if ((args.Length != 2 && args.Length != 3) || args[0] != "--update-completed" ||
            (args.Length == 3 && args[2] != ReopenSettings)) return false;
        helper = args[1];
        reopenSettings = args.Length == 3;
        return true;
    }
}
