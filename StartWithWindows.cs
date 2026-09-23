using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BluetoothBatteryMonitor;

// Start with Windows, as in APIMonitor: a per-user Run entry launches this
// executable at sign-in without administrator rights. Task Manager and
// Settings can disable that entry without deleting it (odd first byte of its
// StartupApproved value), so a disabled entry counts as off.
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "BluetoothBatteryMonitor";

    internal static string Command(string executable) => $"\"{executable}\"";

    // An entry that launches another copy of the executable is not this one's.
    internal static bool IsEnabled(object? runValue, object? approvedValue, string executable) =>
        runValue is string command && string.Equals(command, Command(executable), StringComparison.OrdinalIgnoreCase) &&
        !(approvedValue is byte[] { Length: > 0 } approved && (approved[0] & 1) != 0);

    [SupportedOSPlatform("windows")]
    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return IsEnabled(run?.GetValue(ValueName), approved?.GetValue(ValueName), Executable);
    }

    [SupportedOSPlatform("windows")]
    public static void Set(bool enable)
    {
        if (enable)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(ValueName, Command(Executable), RegistryValueKind.String);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        // Drop any disabled marker so turning this on takes effect and turning
        // it off leaves nothing behind.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
}
