using System.Reflection;
using BluetoothBatteryMonitor;

internal static class AppUpdaterTests
{
    private static void SetField(AppUpdater updater, string name, object? value) =>
        typeof(AppUpdater).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater, value);

    private static void SetProperty(AppUpdater updater, string name, object value) =>
        typeof(AppUpdater).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater, value);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "battery-updater-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var stagedFiles = new List<string>();
        void Stage(AppUpdater updater, bool automatic)
        {
            string path = Path.Combine(folder, Guid.NewGuid() + ".exe");
            File.WriteAllText(path, "test data, never executed");
            stagedFiles.Add(path);
            SetField(updater, "_staged", path);
            SetField(updater, "_available", new Version(AppUpdater.CurrentVersion.Major + 1, 0, 0, 0));
            SetProperty(updater, "AutomaticResult", automatic);
            SetProperty(updater, "Status", "A newer version is ready.");
        }
        try
        {
            foreach (bool automatic in new[] { true, false })
            {
                using var updater = new AppUpdater();
                Stage(updater, automatic);
                check(updater.CanInstall && !updater.Busy, "The completed check awaits an update decision.");
                updater.ConfigurationClosed();
                check(!updater.CanInstall && !updater.Busy && updater.AvailableVersion == null && updater.Status == "",
                    "Closing configuration clears the staged result so the next hourly check can run.");
                updater.ConfigurationClosed();
                check(!updater.CanInstall, "Repeated close cleanup is harmless.");
            }

            using (var cancellation = new CancellationTokenSource())
            using (var updater = new AppUpdater())
            {
                SetField(updater, "_cancellation", cancellation);
                SetProperty(updater, "AutomaticResult", true);
                SetProperty(updater, "Status", "Checking for updates…");
                updater.ConfigurationClosed();
                check(!cancellation.IsCancellationRequested && updater.Busy && updater.Status == "Checking for updates…",
                    "An automatic check continues when configuration closes, allowing its later update prompt.");

                // The download may already be staged while CheckAsync awaits
                // cleanup, before it publishes the completed automatic result.
                Stage(updater, true);
                updater.ConfigurationClosed();
                check(!cancellation.IsCancellationRequested && updater.CanInstall,
                    "Closing during automatic completion preserves the result for its upcoming prompt.");
                SetField(updater, "_cancellation", null);
                updater.ConfigurationClosed();
                check(!updater.CanInstall, "Closing after completion releases that same staged result.");
            }

            using (var cancellation = new CancellationTokenSource())
            using (var updater = new AppUpdater())
            {
                SetField(updater, "_cancellation", cancellation);
                SetProperty(updater, "AutomaticResult", false);
                updater.ConfigurationClosed();
                check(cancellation.IsCancellationRequested, "Closing configuration cancels a manual check.");
                // Also cover a manual result staged just before final cleanup.
                Stage(updater, false);
                updater.ConfigurationClosed();
                check(!updater.CanInstall && updater.Status == "",
                    "A manual result cannot remain staged when its configuration window closes.");
            }

            using (var updater = new AppUpdater())
            {
                Stage(updater, true);
                SetField(updater, "_installing", true);
                updater.ConfigurationClosed();
                check(updater.CanInstall && updater.Installing,
                    "Closing configuration leaves an accepted installation's staged file intact.");
                SetField(updater, "_installing", false);
            }

            // Discard deletes staged downloads off the UI thread.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (stagedFiles.Any(File.Exists)) await Task.Delay(10, timeout.Token);
            check(true, "Discarded staged files are removed from disk.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
