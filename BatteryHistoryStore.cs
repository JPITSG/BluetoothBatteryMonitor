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
}

// All file access and history mutation run on one worker. The tray/UI only
// enqueues observations and reads the small, thread-safe last-charge snapshot.
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
    private readonly Task _worker;

    public BatteryHistoryStore(string directory, Action<string, Exception> logError)
    {
        _directory = directory;
        _logError = logError;
        _worker = Task.Run(ProcessAsync);
    }

    public event Action? LastChargeChanged;

    public DateTimeOffset? GetLastChargedAt(string name) =>
        _lastCharges.TryGetValue(name, out var timestamp) ? timestamp : null;

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
                PublishLastCharge(history);
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

    private void PublishLastCharge(DeviceBatteryHistory history)
    {
        if (history.LastChargedAt is not DateTimeOffset timestamp || GetLastChargedAt(history.DeviceName) == timestamp) return;
        _lastCharges[history.DeviceName] = timestamp;
        LastChargeChanged?.Invoke();
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
