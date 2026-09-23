using BluetoothBatteryMonitor;

internal static class StartWithWindowsTests
{
    public static void Run(Action<bool, string> check)
    {
        const string executable = @"C:\Program Files (x86)\BluetoothBatteryMonitor\BluetoothBatteryMonitor.exe";
        string command = StartWithWindows.Command(executable);
        check(command == "\"" + executable + "\"", "The Run entry quotes the executable path, which can contain spaces.");
        check(StartWithWindows.ValueName == "BluetoothBatteryMonitor", "The Run entry is named after the app, as APIMonitor's is.");
        check(StartWithWindows.IsEnabled(command, null, executable), "An entry for this executable without a Task Manager marker is on.");
        check(StartWithWindows.IsEnabled(command.ToUpperInvariant(), null, executable), "Paths compare without case, as Windows does.");
        check(!StartWithWindows.IsEnabled(null, null, executable), "No entry is off.");
        check(!StartWithWindows.IsEnabled("\"D:\\Old copy\\BluetoothBatteryMonitor.exe\"", null, executable),
            "An entry for another copy of the executable is off, so turning this on replaces it.");
        check(!StartWithWindows.IsEnabled(executable, null, executable), "An entry this app did not write, without quotes, is off.");
        check(!StartWithWindows.IsEnabled(new byte[] { 1 }, null, executable), "A Run value that is not text is off.");
        check(StartWithWindows.IsEnabled(command, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, executable),
            "An entry Task Manager lists as enabled is on.");
        check(!StartWithWindows.IsEnabled(command, new byte[] { 3, 0, 0, 0, 0x88, 0xAA, 0x49, 0x35, 0x11, 0x48, 0xDC, 0x01 }, executable),
            "An entry disabled in Task Manager or Settings is off.");
        check(StartWithWindows.IsEnabled(command, Array.Empty<byte>(), executable) && StartWithWindows.IsEnabled(command, "text", executable),
            "Only a binary marker with an odd first byte disables the entry.");
    }
}
