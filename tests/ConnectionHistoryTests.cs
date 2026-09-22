using System.Text.Json;
using BluetoothBatteryMonitor;

internal static class ConnectionHistoryTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var t0 = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(2));
        const string Connected = ConnectionState.Connected, Disconnected = ConnectionState.Disconnected, Unmonitored = ConnectionState.Unmonitored;

        // Recording keeps changes only, in order, in UTC.
        var history = new DeviceBatteryHistory { DeviceName = "Mouse" };
        check(!history.RecordConnection(Unmonitored, t0) && history.ConnectionChanges.Count == 0,
            "Monitoring cannot stop before anything was observed.");
        check(!history.RecordConnection("sleeping", t0) && !history.RecordConnection(Connected, default),
            "Unknown states and missing times are rejected.");
        check(history.RecordConnection(Connected, t0) && history.ConnectionChanges[0].At.Offset == TimeSpan.Zero &&
            history.ConnectionChanges[0].At == t0, "The first observation is stored in UTC.");
        check(!history.RecordConnection(Connected, t0.AddMinutes(5)) && history.ConnectionChanges.Count == 1,
            "An unchanged state adds nothing.");
        check(history.RecordConnection(Disconnected, t0.AddMinutes(-3)) && history.ConnectionChanges[1].At == t0,
            "A backdated change never precedes the change before it.");
        check(history.ConnectionHistoryStart == t0, "The history start is the first recorded change.");

        // Offline periods as the graph receives them.
        static DeviceBatteryHistory Log(DateTimeOffset start, params (double Minutes, string State)[] changes)
        {
            var log = new DeviceBatteryHistory { DeviceName = "Log" };
            foreach (var (minutes, state) in changes) log.RecordConnection(state, start.AddMinutes(minutes));
            return log;
        }
        var simple = Log(t0, (0, Connected), (10, Disconnected), (25, Connected)).OfflinePeriods(t0);
        check(simple.Length == 1 && simple[0] == new OfflinePeriod(t0.AddMinutes(10), t0.AddMinutes(25), Disconnected),
            "A disconnection becomes one closed offline period.");
        var restart = Log(t0, (0, Connected), (10, Unmonitored), (10.5, Connected)).OfflinePeriods(t0);
        check(restart.Length == 0, "Restarting the app while the device stays connected is not reported as offline.");
        var asleep = Log(t0, (0, Connected), (60, Unmonitored), (540, Connected)).OfflinePeriods(t0);
        check(asleep.Length == 1 && asleep[0].Reason == Unmonitored && asleep[0].To == t0.AddMinutes(540),
            "A night with the PC asleep is offline and marked as not monitored.");
        var mixed = Log(t0, (0, Connected), (30, Disconnected), (60, Unmonitored), (61, Connected)).OfflinePeriods(t0);
        check(mixed.Length == 1 && mixed[0] == new OfflinePeriod(t0.AddMinutes(30), t0.AddMinutes(61), Disconnected),
            "A disconnection that continues while unmonitored is one period, even when the unmonitored part is short.");
        var open = Log(t0, (0, Connected), (30, Disconnected)).OfflinePeriods(t0);
        check(open.Length == 1 && open[0].To == null && open[0].From == t0.AddMinutes(30), "A current disconnection is an open period.");
        var starting = Log(t0, (0, Connected), (30, Unmonitored)).OfflinePeriods(t0);
        check(starting.Length == 1 && starting[0].To == null && starting[0].Reason == Unmonitored,
            "An unmonitored period stays open until the next observation, however short.");
        var windowed = Log(t0, (0, Connected), (10, Disconnected), (20, Connected), (40, Disconnected), (50, Connected), (70, Disconnected))
            .OfflinePeriods(t0.AddMinutes(45));
        check(windowed.Select(period => period.From).SequenceEqual(new[] { t0.AddMinutes(40), t0.AddMinutes(70) }),
            "Only periods overlapping the graph are published, including one that began before it.");
        var zero = Log(t0, (0, Connected), (10, Disconnected), (5, Connected)).OfflinePeriods(t0);
        check(zero.Length == 0, "A period of no duration is not reported.");

        var capped = new DeviceBatteryHistory { DeviceName = "Flaky" };
        for (int i = 0; i < DeviceBatteryHistory.MaximumConnectionChanges + 10; i++)
            capped.RecordConnection(i % 2 == 0 ? Connected : Disconnected, t0.AddMinutes(i));
        check(capped.ConnectionChanges.Count == DeviceBatteryHistory.MaximumConnectionChanges &&
            capped.ConnectionChanges[0].At == t0.AddMinutes(10) && capped.ConnectionHistoryStart == t0.AddMinutes(10),
            "Each device keeps at most 4,000 changes, dropping the oldest, and the history start follows.");

        // Tracker timing: states are dated when they began and recorded once
        // they have lasted, outside the settling time after startup or wake.
        // Like the app's timer, samples arrive every second.
        var recorded = new List<(string Name, string State, DateTimeOffset At)>();
        var tracker = new ConnectionTracker((name, state, at) => recorded.Add((name, state, at)));
        var s = t0;
        double clock = -1;
        DateTimeOffset At(double seconds) => s.AddSeconds(seconds);
        void Hold(double until, params (string, bool)[] devices) { while (clock < until) tracker.Sample(At(++clock), devices); }
        tracker.Settle(At(0));
        Hold(1, ("Mouse", false), ("Keyboard", false));
        Hold(29, ("Mouse", true), ("Keyboard", false));
        check(recorded.Count == 0, "Nothing is recorded while devices are still being found after startup.");
        Hold(30, ("Mouse", true), ("Keyboard", false));
        check(recorded.SequenceEqual(new[] { ("Mouse", Connected, At(2)), ("Keyboard", Disconnected, At(0)) }),
            "After settling, each device's state is recorded from when it began.");
        recorded.Clear();
        Hold(99, ("Mouse", true), ("Keyboard", false));
        Hold(104, ("Mouse", false), ("Keyboard", false));
        Hold(120, ("Mouse", true), ("Keyboard", false));
        check(recorded.Count == 0, "A five-second drop is not recorded.");
        Hold(199, ("Mouse", true), ("Keyboard", false));
        Hold(209, ("Mouse", false), ("Keyboard", false));
        check(recorded.Count == 0, "A disconnection is not recorded before it has lasted ten seconds.");
        Hold(210, ("Mouse", false), ("Keyboard", false));
        check(recorded.SequenceEqual(new[] { ("Mouse", Disconnected, At(200)) }), "A lasting disconnection is dated from when it began.");
        Hold(299, ("Mouse", false), ("Keyboard", false));
        Hold(310, ("Mouse", true), ("Keyboard", true));
        check(recorded.Skip(1).SequenceEqual(new[] { ("Mouse", Connected, At(300)), ("Keyboard", Connected, At(300)) }),
            "Reconnections are recorded from when they began.");
        recorded.Clear();
        Hold(399, ("Mouse", true), ("Keyboard", true));
        tracker.Settle(At(400));
        Hold(419, ("Mouse", false), ("Keyboard", true));
        Hold(440, ("Mouse", true), ("Keyboard", true));
        check(recorded.Count == 0, "Devices released and found again during a configuration reload are not disconnections.");

        Hold(500, ("Mouse", true), ("Keyboard", true));
        double resumed = clock = 500 + 8 * 3600;
        tracker.Sample(At(resumed), new[] { ("Mouse", true), ("Keyboard", true) });
        check(recorded.SequenceEqual(new[] { ("Mouse", Unmonitored, At(500)), ("Keyboard", Unmonitored, At(500)) }),
            "A long pause between samples means the PC slept: monitoring stopped at the last sample.");
        recorded.Clear();
        Hold(resumed + 11, ("Mouse", false), ("Keyboard", true));
        Hold(resumed + 29, ("Mouse", true), ("Keyboard", true));
        check(recorded.Count == 0, "Nothing is recorded while devices reconnect after waking.");
        Hold(resumed + 30, ("Mouse", true), ("Keyboard", true));
        check(recorded.SequenceEqual(new[] { ("Mouse", Connected, At(resumed + 12)), ("Keyboard", Connected, At(resumed)) }),
            "After waking, states are recorded from when they were first seen.");
        recorded.Clear();
        Hold(resumed + 31, ("Mouse", true));
        check(recorded.SequenceEqual(new[] { ("Keyboard", Unmonitored, At(resumed + 31)) }),
            "A device removed from monitoring stops being monitored.");
        recorded.Clear();
        Hold(resumed + 32, ("Mouse", true), ("Mouse", false), ("MOUSE", false));
        check(recorded.Count == 0, "Only the first sample of a device name counts.");
        tracker.Stop(At(resumed + 33));
        check(recorded.SequenceEqual(new[] { ("Mouse", Unmonitored, At(resumed + 33)) }), "Exit, sign-out and sleep stop monitoring.");
        recorded.Clear();
        clock = resumed + 33;
        Hold(resumed + 62, ("Mouse", true));
        check(recorded.Count == 0, "Samples taken while going to sleep are not recorded.");
        Hold(resumed + 63, ("Mouse", true));
        check(recorded.SequenceEqual(new[] { ("Mouse", Connected, At(resumed + 34)) }),
            "If the PC stays awake after all, recording resumes after settling.");

        // Persistence, publication, and repair of stored changes.
        string root = Path.Combine(Path.GetTempPath(), "connection-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var errors = new List<Exception>();
        void Error(string operation, Exception error) => errors.Add(error);
        var read = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            string mousePath;
            OfflinePeriod[]? published;
            using (var store = new BatteryHistoryStore(root, Error))
            {
                int notifications = 0;
                store.Changed += () => notifications++;
                store.Record("Mouse", 80, t0);
                store.RecordConnection("Mouse", Connected, t0);
                store.RecordConnection("Mouse", Disconnected, t0.AddMinutes(30));
                store.RecordConnection("Mouse", Connected, t0.AddMinutes(45));
                store.Record("Mouse", 79, t0.AddHours(1));
                store.RecordConnection("MOUSE", Disconnected, t0.AddHours(2));
                await store.FlushAsync();
                mousePath = store.FilePath("Mouse");
                var saved = JsonSerializer.Deserialize<DeviceBatteryHistory>(await File.ReadAllTextAsync(mousePath), read)!;
                check(saved.ConnectionChanges.Select(change => change.State).SequenceEqual(new[] { Connected, Disconnected, Connected, Disconnected }) &&
                    saved.Entries.Count == 2, "Connection changes are saved beside the readings.");
                check((await File.ReadAllTextAsync(mousePath)).Contains("\"state\": \"disconnected\""), "States are stored as readable words.");
                published = store.GetOffline("Mouse");
                check(published != null && published.SequenceEqual(new[] {
                    new OfflinePeriod(t0.AddMinutes(30), t0.AddMinutes(45), Disconnected),
                    new OfflinePeriod(t0.AddHours(2), null, Disconnected) }) && store.GetConnectionHistoryStart("mouse") == t0,
                    "The graph receives offline periods since its first reading and when recording began.");
                int before = notifications;
                var modified = File.GetLastWriteTimeUtc(mousePath);
                store.RecordConnection("Mouse", Disconnected, t0.AddHours(3));
                await store.FlushAsync();
                check(notifications == before && ReferenceEquals(store.GetOffline("Mouse"), published) && File.GetLastWriteTimeUtc(mousePath) == modified,
                    "An unchanged state keeps the snapshot and writes nothing.");
                store.Record("Keyboard", 50, t0);
                await store.FlushAsync();
                check(store.GetOffline("Keyboard") is { Length: 0 } && store.GetConnectionHistoryStart("Keyboard") == null,
                    "A device without recorded changes has no offline periods or history start.");
            }
            using (var restarted = new BatteryHistoryStore(root, Error))
            {
                restarted.Load("Mouse");
                await restarted.FlushAsync();
                check(restarted.GetOffline("Mouse")!.SequenceEqual(published!), "Offline periods are restored after restarting.");
                restarted.RecordConnection("Mouse", Unmonitored, t0.AddHours(4));
                restarted.RecordConnection("Mouse", Unmonitored, t0.AddHours(5));
                restarted.Record("Mouse", 82, t0.AddHours(6));
            }
            var final = JsonSerializer.Deserialize<DeviceBatteryHistory>(await File.ReadAllTextAsync(mousePath), read)!;
            check(final.ConnectionChanges.Count == 5 && final.ConnectionChanges[^1] == new ConnectionChange(t0.AddHours(4).ToUniversalTime(), Unmonitored),
                "Stopping twice records one stop, and exit drains pending changes.");

            string legacyPath = Path.Combine(root, "legacy.json");
            using (var store = new BatteryHistoryStore(root, Error))
            {
                legacyPath = store.FilePath("Legacy");
                await File.WriteAllTextAsync(legacyPath, "{\"formatVersion\":1,\"deviceName\":\"Legacy\",\"entries\":[{\"observedAt\":\"2026-09-20T08:00:00Z\",\"percentage\":70}]}");
                string nullPath = store.FilePath("Null");
                await File.WriteAllTextAsync(nullPath, "{\"formatVersion\":1,\"deviceName\":\"Null\",\"connectionChanges\":null,\"entries\":[{\"observedAt\":\"2026-09-20T08:00:00Z\",\"percentage\":70}]}");
                string damagedPath = store.FilePath("Damaged");
                await File.WriteAllTextAsync(damagedPath, "{\"formatVersion\":1,\"deviceName\":\"Damaged\",\"entries\":[{\"observedAt\":\"2026-09-20T08:00:00Z\",\"percentage\":70}]," +
                    "\"connectionChanges\":[{\"at\":\"2026-09-20T07:00:00Z\",\"state\":\"unmonitored\"},null,{\"state\":\"connected\"}," +
                    "{\"at\":\"2026-09-20T08:00:00Z\",\"state\":\"connected\"},{\"at\":\"2026-09-20T08:05:00Z\",\"state\":\"connected\"}," +
                    "{\"at\":\"2026-09-20T09:00:00Z\",\"state\":\"asleep\"},{\"at\":\"2026-09-20T07:30:00Z\",\"state\":\"disconnected\"}]}");
                foreach (var name in new[] { "Legacy", "Null", "Damaged" }) store.Load(name);
                await store.FlushAsync();
                check(errors.Count == 0 && store.GetTrend("Legacy")?.Length == 1 && store.GetOffline("Legacy") is { Length: 0 } &&
                    store.GetTrend("Null")?.Length == 1, "Logs written before connections were recorded load unchanged.");
                store.RecordConnection("Damaged", Connected, DateTimeOffset.Parse("2026-09-20T10:00:00Z"));
                await store.FlushAsync();
                var repaired = JsonSerializer.Deserialize<DeviceBatteryHistory>(await File.ReadAllTextAsync(damagedPath), read)!;
                check(errors.Count == 0 && repaired.Entries.Count == 1 && repaired.ConnectionChanges.SequenceEqual(new[] {
                    new ConnectionChange(DateTimeOffset.Parse("2026-09-20T08:00:00Z"), Connected),
                    new ConnectionChange(DateTimeOffset.Parse("2026-09-20T08:00:00Z"), Disconnected),
                    new ConnectionChange(DateTimeOffset.Parse("2026-09-20T10:00:00Z"), Connected) }),
                    "Damaged connection changes are repaired without discarding the battery readings.");
            }
            check(!Directory.GetFiles(root, "*.corrupt-*").Any(), "No history is set aside as corrupt.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
