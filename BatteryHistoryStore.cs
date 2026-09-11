using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor;

internal sealed record BatteryHistoryEntry(DateTimeOffset ObservedAt, int Percentage);

// The readings plotted in configuration: the current discharge since the last
// charge ended, or the current charge since it began, whichever is nearer.
internal static class BatteryTrend
{
    // A reversal of fewer points than this is reporting noise, not a new charge
    // or discharge. It matches the charge detection threshold.
    public const int TurningPointThreshold = 5;

    public static BatteryHistoryEntry[] Select(IReadOnlyList<BatteryHistoryEntry> entries)
    {
        // Zero is the disconnected sentinel and is never plotted.
        var readings = entries.Where(entry => entry.Percentage > 0).ToList();
        if (readings.Count == 0) return Array.Empty<BatteryHistoryEntry>();
        int last = readings.Count - 1;
        // The direction is set by the nearest earlier reading that differs from
        // the newest one by at least the threshold. Without one, nothing has
        // happened yet and every reading is shown.
        int direction = 0;
        for (int i = last - 1; i >= 0 && direction == 0; i--)
        {
            int difference = readings[last].Percentage - readings[i].Percentage;
            if (Math.Abs(difference) >= TurningPointThreshold) direction = Math.Sign(difference);
        }
        if (direction == 0) return readings.ToArray();
        // Walk back to the turning point: the peak before a discharge or the
        // trough before a charge, where earlier readings are at least the
        // threshold beyond it in the opposite direction.
        int start = last;
        for (int i = last - 1; i >= 0; i--)
        {
            int beyond = (readings[i].Percentage - readings[start].Percentage) * direction;
            if (beyond >= TurningPointThreshold) break;
            if (beyond <= 0) start = i;
        }
        return readings.Skip(start).ToArray();
    }
}

internal sealed class DeviceBatteryHistory
{
    public const int MaximumEntries = 1000;
    public int FormatVersion { get; init; } = 1;
    public string DeviceName { get; init; } = "";
    public List<BatteryHistoryEntry> Entries { get; init; } = new();
    public DateTimeOffset? LastChargedAt { get; set; }

    public bool Record(int percentage, DateTimeOffset observedAt)
    {
        if (percentage is < 0 or > 100 || observedAt == default ||
            (Entries.Count > 0 && Entries[^1].Percentage == percentage)) return false;

        // Zero is a disconnected sentinel in this app. Retain it in the log,
        // but compare charging against the previous usable battery reading.
        var previous = Entries.LastOrDefault(entry => entry.Percentage > 0);
        if (percentage > 0 && previous != null && percentage - previous.Percentage >= 5)
            LastChargedAt = observedAt.ToUniversalTime();

        Entries.Add(new BatteryHistoryEntry(observedAt.ToUniversalTime(), percentage));
        if (Entries.Count > MaximumEntries) Entries.RemoveRange(0, Entries.Count - MaximumEntries);
        return true;
    }

    public BatteryHistoryEntry[] CurrentTrend() => BatteryTrend.Select(Entries);
}

// All file access and history mutation run on one worker. The tray/UI only
// enqueues observations and reads the small, thread-safe published snapshots.
internal sealed class BatteryHistoryStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly string _directory;
    private readonly Action<string, Exception> _logError;
    private readonly Channel<HistoryCommand> _commands = Channel.CreateUnbounded<HistoryCommand>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Dictionary<string, DeviceBatteryHistory> _histories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCharges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BatteryHistoryEntry[]> _trends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;

    public BatteryHistoryStore(string directory, Action<string, Exception> logError)
    {
        _directory = directory;
        _logError = logError;
        _worker = Task.Run(ProcessAsync);
    }

    // Raised on the worker whenever a device's last charge or trend snapshot changes.
    public event Action? Changed;

    public DateTimeOffset? GetLastChargedAt(string name) =>
        _lastCharges.TryGetValue(name, out var timestamp) ? timestamp : null;

    // Chronological readings of the current discharge or charge; empty until
    // a device has a usable reading and null before its history is loaded.
    public BatteryHistoryEntry[]? GetTrend(string name) =>
        _trends.TryGetValue(name, out var trend) ? trend : null;

    public void Load(string name) => _commands.Writer.TryWrite(new HistoryCommand(name));

    public void Record(string name, int percentage, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(name) || percentage is < 0 or > 100 || observedAt == default) return;
        _commands.Writer.TryWrite(new HistoryCommand(name, percentage, observedAt));
    }

    public Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _commands.Writer.TryWrite(new HistoryCommand("", Completion: completion)) ? completion.Task : _worker;
    }

    internal string FilePath(string name) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()))).ToLowerInvariant() + ".json");

    private async Task ProcessAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (command.Completion != null)
            {
                await SaveDirtyAsync().ConfigureAwait(false);
                command.Completion.SetResult();
                continue;
            }
            try
            {
                var history = await LoadAsync(command.Name).ConfigureAwait(false);
                if (command.Percentage is int percentage && history.Record(percentage, command.ObservedAt))
                    _dirty.Add(command.Name);
                Publish(history);
                if (_dirty.Contains(command.Name)) await SaveAsync(history).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _logError("Battery history", error);
            }
        }
        await SaveDirtyAsync().ConfigureAwait(false);
    }

    private async Task<DeviceBatteryHistory> LoadAsync(string name)
    {
        if (_histories.TryGetValue(name, out var history)) return history;
        string path = FilePath(name);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("Battery history exceeds the file size limit.");
            history = await JsonSerializer.DeserializeAsync<DeviceBatteryHistory>(stream, JsonOptions).ConfigureAwait(false);
            if (history == null || history.FormatVersion != 1 ||
                !string.Equals(history.DeviceName, name, StringComparison.OrdinalIgnoreCase) || history.Entries == null ||
                history.Entries.Count > DeviceBatteryHistory.MaximumEntries ||
                history.Entries.Any(entry => entry == null || entry.Percentage is < 0 or > 100 || entry.ObservedAt == default) ||
                history.LastChargedAt == default(DateTimeOffset))
                throw new InvalidDataException("Invalid battery history contents.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            history = new DeviceBatteryHistory { DeviceName = name };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            // Preserve damaged data for recovery. Access/sharing errors are NOT
            // treated as empty history: those retry on the next observation.
            _logError("Invalid battery history; preserving a backup", error);
            File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
            history = new DeviceBatteryHistory { DeviceName = name };
        }
        _histories.Add(name, history);
        return history;
    }

    private void Publish(DeviceBatteryHistory history)
    {
        bool changed = false;
        if (history.LastChargedAt is DateTimeOffset timestamp && GetLastChargedAt(history.DeviceName) != timestamp)
        {
            _lastCharges[history.DeviceName] = timestamp;
            changed = true;
        }
        var trend = history.CurrentTrend();
        if (GetTrend(history.DeviceName) is not { } previous || !previous.SequenceEqual(trend))
        {
            // Replaced only on change, so an unchanged snapshot keeps its identity
            // and the dialog can skip repeated status messages.
            _trends[history.DeviceName] = trend;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    private async Task SaveAsync(DeviceBatteryHistory history)
    {
        Directory.CreateDirectory(_directory);
        string path = FilePath(history.DeviceName);
        string temporaryPath = path + ".tmp";
        // Replace only after the complete new document is on disk. Readers and
        // a restarted app see either the previous log or the complete new log.
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, history, JsonOptions).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
        _dirty.Remove(history.DeviceName);
    }

    private async Task SaveDirtyAsync()
    {
        foreach (string name in _dirty.ToArray())
        {
            try { await SaveAsync(_histories[name]).ConfigureAwait(false); }
            catch (Exception error) { _logError("Save battery history", error); }
        }
    }

    public void Dispose()
    {
        _commands.Writer.TryComplete();
        // The worker never waits for the UI, so normal exit/update can drain
        // pending saves without losing the last reading or deadlocking.
        _worker.GetAwaiter().GetResult();
    }

    private sealed record HistoryCommand(string Name, int? Percentage = null,
        DateTimeOffset ObservedAt = default, TaskCompletionSource? Completion = null);
}
