using System.Text.Json;
using BluetoothBatteryMonitor;

internal static class BatteryHistoryTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.FromHours(2));
        var history = new DeviceBatteryHistory { DeviceName = "Mouse" };
        check(history.Record(60, start) && history.LastChargedAt == null, "The first battery sample is logged without inventing a charge.");
        check(!history.Record(60, start.AddMinutes(1)) && history.Entries.Count == 1, "Repeated percentages do not add history entries.");
        check(!history.Record(-1, start) && !history.Record(101, start) && !history.Record(50, default), "Reject invalid percentages and missing observation times.");
        history.Record(64, start.AddMinutes(2));
        check(history.LastChargedAt == null, "A four-point rise does not reach the charge threshold.");
        history.Record(69, start.AddMinutes(3));
        var charged = start.AddMinutes(3).ToUniversalTime();
        check(history.LastChargedAt == charged && history.LastChargedAt.Value.Offset == TimeSpan.Zero, "A five-point rise records its observation time in UTC.");
        history.Record(68, start.AddMinutes(4));
        history.Record(0, start.AddMinutes(5));
        history.Record(68, start.AddMinutes(6));
        check(history.LastChargedAt == charged, "A disconnected zero followed by the same usable percentage must not invent a charge.");
        history.Record(0, start.AddMinutes(7));
        history.Record(73, start.AddMinutes(8));
        check(history.LastChargedAt == start.AddMinutes(8), "A five-point increase across a disconnected interval still detects charging.");
        var initiallyEmpty = new DeviceBatteryHistory { DeviceName = "Empty" };
        initiallyEmpty.Record(0, start);
        initiallyEmpty.Record(80, start.AddMinutes(1));
        check(initiallyEmpty.LastChargedAt == null && initiallyEmpty.Entries.Count == 2, "Zero is retained in history but cannot establish a charging baseline.");

        // The configuration graph covers the current discharge or charge only.
        static int[] Trend(params int[] percentages) => BatteryTrend.Select(percentages
            .Select((percentage, index) => new BatteryHistoryEntry(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddHours(index), percentage))
            .ToList()).Select(entry => entry.Percentage).ToArray();
        check(BatteryTrend.TurningPointThreshold == 5, "Trend turning points use the same five-point threshold as charge detection.");
        check(Trend().Length == 0 && Trend(0).Length == 0, "No usable reading produces an empty trend rather than a fabricated point.");
        check(Trend(64).SequenceEqual(new[] { 64 }), "A single reading is the whole trend.");
        check(Trend(84, 85, 84).SequenceEqual(new[] { 84, 85, 84 }), "Readings within the threshold have no direction yet, so every reading is shown.");
        check(Trend(60, 65, 70, 100, 95, 90).SequenceEqual(new[] { 100, 95, 90 }), "A discharge starts at the peak where the last charge ended.");
        check(Trend(50, 40, 30, 35, 45).SequenceEqual(new[] { 30, 35, 45 }), "A charge starts at the trough where charging began.");
        check(Trend(90, 85, 84, 85).SequenceEqual(new[] { 90, 85, 84, 85 }), "A one-point rise during a discharge is noise, not a new charge.");
        check(Trend(100, 99, 100, 95, 60).SequenceEqual(new[] { 100, 99, 100, 95, 60 }), "A sub-threshold top-up does not restart the discharge.");
        check(Trend(68, 0, 68, 60).SequenceEqual(new[] { 68, 68, 60 }), "Disconnected zeroes are skipped without splitting the discharge.");
        check(Trend(40, 90, 93, 95, 94, 93, 92).SequenceEqual(new[] { 40, 90, 93, 95, 94, 93, 92 }), "A charge remains current until the battery falls five points below its peak.");
        check(Trend(40, 90, 93, 95, 94, 93, 92, 90).SequenceEqual(new[] { 95, 94, 93, 92, 90 }), "Five points below the peak, the trend becomes the discharge since that peak.");
        var trendHistory = new DeviceBatteryHistory { DeviceName = "Trend" };
        foreach (var (percentage, minute) in new[] { (70, 0), (65, 1), (0, 2), (65, 3), (60, 4) }) trendHistory.Record(percentage, start.AddMinutes(minute));
        var trend = trendHistory.CurrentTrend();
        check(trend.Select(entry => entry.Percentage).SequenceEqual(new[] { 70, 65, 65, 60 }) && trend[0].ObservedAt == start && trend[^1].ObservedAt == start.AddMinutes(4),
            "A device history exposes its current trend in observation order with timestamps.");
        var snapshot = new BatteryHistoryEntry[] { new(start, 60) };
        check(new DeviceStatus("Mouse", true, 60, null, snapshot) == new DeviceStatus("Mouse", true, 60, null, snapshot) &&
            new DeviceStatus("Mouse", true, 60, null, snapshot) != new DeviceStatus("Mouse", true, 60, null, new BatteryHistoryEntry[] { new(start, 60) }),
            "Configuration resends statuses only when a device receives a new trend snapshot.");

        var lastCharge = history.LastChargedAt;
        for (int i = 0; i < 1010; i++) history.Record(60 + i % 2, start.AddMinutes(10 + i));
        check(history.Entries.Count == 1000, "Each device retains at most 1,000 changed readings.");
        check(history.Entries[0].ObservedAt == start.AddMinutes(20) && history.Entries[^1].ObservedAt == start.AddMinutes(1019), "Retention removes only the oldest readings, preserving observation order.");
        check(history.LastChargedAt == lastCharge, "Last charge remains known after its evidence ages out of the 1,000-entry log.");

        string root = Path.Combine(Path.GetTempPath(), "battery-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var errors = new List<Exception>();
        void Log(string operation, Exception error) => errors.Add(error);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        async Task<DeviceBatteryHistory> Read(string path) => JsonSerializer.Deserialize<DeviceBatteryHistory>(await File.ReadAllTextAsync(path), options)!;
        try
        {
            string mousePath;
            using (var store = new BatteryHistoryStore(root, Log))
            {
                store.Load("Mouse");
                await store.FlushAsync();
                check(Directory.GetFiles(root).Length == 0 && store.GetLastChargedAt("Mouse") == null, "Loading a monitored device does not create a fabricated history sample.");
                mousePath = store.FilePath("Mouse");
                check(mousePath == store.FilePath("MOUSE") && mousePath != store.FilePath("Keyboard"), "History identities survive casing changes and distinguish devices.");
                check(Path.GetDirectoryName(store.FilePath("../../con:<>/\\name")) == root, "Device names cannot escape the history directory or create invalid Windows filenames.");
                check(store.GetTrend("Mouse") is { Length: 0 } && store.GetTrend("Keyboard") == null, "A loaded device publishes an empty trend; an unloaded device has none.");
                int notifications = 0;
                store.Changed += () => notifications++;
                store.Record("Mouse", 60, start);
                store.Record("Mouse", 64, start.AddMinutes(1));
                store.Record("MOUSE", 69, start.AddMinutes(2));
                store.Record("Keyboard", 80, start);
                await store.FlushAsync();
                var saved = await Read(mousePath);
                check(saved.Entries.Select(entry => entry.Percentage).SequenceEqual(new[] { 60, 64, 69 }), "Background saves preserve every changed percentage in order, including mixed-case callbacks.");
                check(saved.LastChargedAt == start.AddMinutes(2) && notifications == 4, "Persist charge metadata and notify the open modal for every logged reading.");
                var mouseTrend = store.GetTrend("mouse");
                check(mouseTrend != null && mouseTrend.Select(entry => entry.Percentage).SequenceEqual(new[] { 60, 64, 69 }) && mouseTrend[0].ObservedAt == start,
                    "The published trend snapshot follows the log, including mixed-case callbacks.");
                check((await Read(store.FilePath("Keyboard"))).Entries.Single().Percentage == 80 && store.GetLastChargedAt("Keyboard") == null, "Device logs and charging state remain independent.");
                check(!Directory.GetFiles(root, "*.tmp").Any(), "A successful atomic save leaves no temporary document.");
                var bytes = await File.ReadAllBytesAsync(mousePath);
                var modified = File.GetLastWriteTimeUtc(mousePath);
                store.Record("Mouse", 69, start.AddMinutes(9));
                await store.FlushAsync();
                check(bytes.SequenceEqual(await File.ReadAllBytesAsync(mousePath)) && File.GetLastWriteTimeUtc(mousePath) == modified, "An unchanged percentage performs no disk write.");
                check(notifications == 4 && ReferenceEquals(store.GetTrend("Mouse"), mouseTrend), "An unchanged percentage keeps the same trend snapshot and sends no notification.");
            }

            using (var restarted = new BatteryHistoryStore(root, Log))
            {
                int loadedNotifications = 0;
                restarted.Changed += () => loadedNotifications++;
                restarted.Load("mouse");
                await restarted.FlushAsync();
                check(restarted.GetLastChargedAt("MOUSE") == start.AddMinutes(2) && loadedNotifications == 1, "Startup restores last charge asynchronously, including while the device is disconnected.");
                check(restarted.GetTrend("MOUSE")?.Select(entry => entry.Percentage).SequenceEqual(new[] { 60, 64, 69 }) == true, "Startup restores the trend graph from the saved log in one notification.");
                var modified = File.GetLastWriteTimeUtc(mousePath);
                restarted.Record("Mouse", 69, start.AddHours(1));
                await restarted.FlushAsync();
                check((await Read(mousePath)).Entries.Count == 3 && File.GetLastWriteTimeUtc(mousePath) == modified, "Restarting the app does not duplicate or rewrite the previous battery percentage.");
                restarted.Record("Mouse", 74, start.AddHours(2));
                // Dispose must flush the last reading even without FlushAsync.
            }
            check((await Read(mousePath)).LastChargedAt == start.AddHours(2) && (await Read(mousePath)).Entries.Count == 4, "Exit/update drains pending observations and persists a charge compared with the pre-restart reading.");
            check(errors.Count == 0, "Normal recording, startup, and shutdown complete without history errors.");

            using (var recovery = new BatteryHistoryStore(root, Log))
            {
                string brokenPath = recovery.FilePath("Broken");
                await File.WriteAllTextAsync(brokenPath, "{broken json");
                recovery.Record("Broken", 50, start);
                await recovery.FlushAsync();
                check((await Read(brokenPath)).Entries.Single().Percentage == 50, "A damaged log does not prevent future logging.");
                string backup = Directory.GetFiles(root, Path.GetFileName(brokenPath) + ".corrupt-*").Single();
                check(await File.ReadAllTextAsync(backup) == "{broken json" && errors.Count == 1, "Preserve damaged history in a backup and report the problem.");
                string invalidPath = recovery.FilePath("Invalid");
                await File.WriteAllTextAsync(invalidPath, "{\"formatVersion\":1,\"deviceName\":\"Invalid\",\"entries\":[{\"percentage\":200,\"observedAt\":\"2026-09-07T09:00:00Z\"}]}");
                recovery.Record("Invalid", 50, start);
                await recovery.FlushAsync();
                check((await Read(invalidPath)).Entries.Single().Percentage == 50 && errors.Count == 2, "Invalid stored percentages cannot fabricate last-charge evidence.");

                // A non-file path simulates an inaccessible history. It must not
                // be replaced with a fresh document as if it were absent.
                string blocked = recovery.FilePath("Inaccessible");
                Directory.CreateDirectory(blocked);
                recovery.Record("Inaccessible", 50, start);
                await recovery.FlushAsync();
                check(Directory.Exists(blocked) && errors.Count == 3, "An unreadable history is reported and not overwritten.");
                Directory.Delete(blocked);
                recovery.Record("Inaccessible", 50, start);
                await recovery.FlushAsync();
                check((await Read(blocked)).Entries.Single().Percentage == 50, "Read failures can recover on a later observation.");
            }

            string blockedDirectory = Path.Combine(root, "blocked-directory");
            await File.WriteAllTextAsync(blockedDirectory, "blocks directory creation");
            using (var retry = new BatteryHistoryStore(blockedDirectory, Log))
            {
                retry.Record("Mouse", 40, start);
                await retry.FlushAsync();
                check(errors.Count > 3 && File.Exists(blockedDirectory), "A save failure is reported without damaging an existing file.");
                File.Delete(blockedDirectory);
                retry.Record("Mouse", 40, start.AddMinutes(1));
                await retry.FlushAsync();
                var saved = await Read(retry.FilePath("Mouse"));
                check(saved.Entries.Count == 1 && saved.Entries[0].ObservedAt == start, "Retry an unsaved change even when the next percentage is unchanged.");
            }

            // Exercise retention on disk without 1,000 physical writes.
            using (var retained = new BatteryHistoryStore(root, Log))
            {
                await File.WriteAllTextAsync(retained.FilePath("Mouse"), JsonSerializer.Serialize(history, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                retained.Record("Mouse", 62, start.AddDays(1));
                await retained.FlushAsync();
                var saved = await Read(retained.FilePath("Mouse"));
                check(saved.Entries.Count == 1000 && saved.Entries[^1].Percentage == 62 && saved.LastChargedAt == lastCharge, "The retention cap and older last-charge metadata survive loading and subsequent saves.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
