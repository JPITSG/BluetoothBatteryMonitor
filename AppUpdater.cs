using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BluetoothBatteryMonitor;

internal sealed class AppUpdater : IDisposable
{
    internal static AppUpdater Instance { get; } = new();
    private const string RegistryPath = @"SOFTWARE\JPIT\BluetoothBatteryMonitor";
    private const string DownloadUrl = "https://github.com/JPITSG/BluetoothBatteryMonitor/raw/refs/heads/main/release/BluetoothBatteryMonitor.exe";
    private const long MaximumSize = 256L * 1024 * 1024;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 * 60 * 1000 };
    private CancellationTokenSource? _cancellation;
    private string? _staged;
    private Version? _available;
    private bool _installing;
    internal event Action? Changed;
    internal event Action? UpdateAvailable;
    internal string Status { get; private set; } = "";
    internal long? DownloadKilobytesPerSecond { get; private set; }
    internal bool Busy => _cancellation != null || _installing;
    internal bool Installing => _installing;
    internal bool CanInstall => _staged != null && _available >= CurrentVersion;
    internal string? AvailableVersion => _available?.ToString(3);
    internal void Dismiss() { if (Busy) return; Discard(); Status = ""; Changed?.Invoke(); }
    internal bool AutomaticResult { get; private set; }
    // The SDK stamps the assembly and Windows file version from the same project
    // version. Avoid parsing the 75 MB executable for every progress notification.
    internal static Version CurrentVersion { get; } = typeof(AppUpdater).Assembly.GetName().Version!;
    internal static string DisplayVersion => CurrentVersion.ToString(3);
    internal bool AutoCheck
    {
        get => (int?)Registry.GetValue(@"HKEY_CURRENT_USER\" + RegistryPath, "AutoCheckForUpdates", 1) != 0;
        set { using var key = Registry.CurrentUser.CreateSubKey(RegistryPath); key.SetValue("AutoCheckForUpdates", value ? 1 : 0); }
    }
    internal void StatusAfterUpdate()
    {
        Status = $"Successfully updated to v{DisplayVersion}.";
        _timer.Tick += async (_, _) => await CheckAsync(true);
        _timer.Start();
    }
    internal void Start()
    {
        _timer.Tick += async (_, _) => await CheckAsync(true);
        _timer.Start();
        _ = CheckAsync(true);
    }
    internal static Version ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart < 1 || info.ProductName != "BluetoothBatteryMonitor")
            throw new InvalidDataException("The download is not a versioned Bluetooth Battery Monitor application.");
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }
    internal async Task CheckAsync(bool automatic)
    {
        if (Busy || (automatic && (!AutoCheck || CanInstall)))
        {
            // A manual click during an automatic check must still get an acknowledgement.
            Changed?.Invoke();
            return;
        }
        Discard();
        DownloadKilobytesPerSecond = null;
        AutomaticResult = automatic;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _cancellation = cancellation;
        bool acceptingProgress = true;
        string path = Path.Combine(Path.GetTempPath(), "BluetoothBatteryMonitor-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            Status = "Checking for updates…";
            Changed?.Invoke();
            var progress = new Progress<DownloadProgress>(download =>
            {
                // Progress can already be queued when cancellation/completion wins.
                if (!acceptingProgress || !ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested) return;
                DownloadKilobytesPerSecond = download.KilobytesPerSecond;
                Status = $"Downloading… {download.Percent}% ({download.KilobytesPerSecond} KB/s)";
                Changed?.Invoke();
            });
            _available = await Task.Run(() => UpdateDownload.StageAsync(
                _http, DownloadUrl, path, MaximumSize, ValidateDownload, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            var current = CurrentVersion;
            Status = $"Installed: v{current.ToString(3)} · Available: v{_available.ToString(3)}. " +
                (_available > current ? "A newer version is ready." : _available == current ? "You're up to date." : "The repository version is older; downgrades are disabled.");
            string? ignored = Registry.GetValue(@"HKEY_CURRENT_USER\" + RegistryPath, "IgnoredUpdateVersion", "") as string;
            if (automatic && (!AutoCheck || _available <= current || ignored == _available.ToString())) Status = "";
            else if (_available >= current) { _staged = path; path = ""; }
        }
        catch (OperationCanceledException) { Status = automatic ? "" : "Update check cancelled or timed out."; }
        catch (Exception ex) { Status = automatic ? "" : "Update failed: " + ex.Message; }
        finally
        {
            acceptingProgress = false;
            DownloadKilobytesPerSecond = null;
            await Task.Run(() => Delete(path));
            _cancellation = null;
            Changed?.Invoke();
        }
        if (automatic && CanInstall) UpdateAvailable?.Invoke();
    }
    internal void Cancel() => _cancellation?.Cancel();
    internal void Ignore()
    {
        if (Busy || _available == null) return;
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("IgnoredUpdateVersion", _available.ToString());
        Discard(); Status = ""; Changed?.Invoke();
    }
    internal void Discard()
    {
        if (_installing) return;
        var staged = _staged;
        _staged = null;
        _available = null;
        if (staged != null) _ = Task.Run(() => Delete(staged));
    }
    private static void Delete(string? path) { try { if (!string.IsNullOrEmpty(path)) File.Delete(path); } catch { } }
    private static Version ValidateDownload(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader == null)
            throw new InvalidDataException("The update is not a valid 64-bit executable.");
        return ReadVersion(path);
    }
    internal async Task InstallAsync(bool reopenSettings)
    {
        if (Busy || !CanInstall) return;
        var staged = _staged!;
        var available = _available;
        var executable = Application.ExecutablePath;
        _installing = true;
        Status = "Starting update…";
        Changed?.Invoke();
        string helper = Path.Combine(Path.GetTempPath(), "BluetoothBatteryMonitor-helper-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            // Antivirus scanning/version validation and copying the helper can take
            // seconds. Keep the dialog's message pump free throughout this work.
            var start = await Task.Run(() =>
            {
                if (ValidateDownload(staged) != available) throw new InvalidDataException("The staged update changed. Check again.");
                File.Copy(executable, helper);
                var info = new ProcessStartInfo(helper) { UseShellExecute = true };
                string probe = Path.Combine(Path.GetDirectoryName(executable)!, Guid.NewGuid() + ".tmp");
                try { using (File.Create(probe)) { } File.Delete(probe); }
                catch (UnauthorizedAccessException) { info.Verb = "runas"; }
                foreach (var argument in UpdateLaunchArguments.Apply(Environment.ProcessId, executable, staged, reopenSettings))
                    info.ArgumentList.Add(argument);
                return info;
            });
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BluetoothBatteryMonitor_UpdateReady_" + Environment.ProcessId);
            using var helperProcess = Process.Start(start) ?? throw new IOException("Could not start the updater.");
            if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(20))))
                throw new IOException("The updater did not become ready. The application is still running.");
            _staged = null;
            Application.Exit();
        }
        catch (Exception ex)
        {
            await Task.Run(() => Delete(helper));
            Status = "Update failed: " + ex.Message;
        }
        finally
        {
            _installing = false;
            Changed?.Invoke();
        }
    }
    internal static bool HandleCommandLine(string[] args)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        string? backup = null;
        bool replaced = false;
        try
        {
            if (!UpdateLaunchArguments.TryReadApply(args, out int pid, out bool reopenSettings))
                throw new ArgumentException("Invalid update arguments.");
            string target = Path.GetFullPath(args[2]);
            string staged = Path.GetFullPath(args[3]);
            if (ReadVersion(staged) < ReadVersion(target)) throw new InvalidDataException("Downgrades are disabled.");
            using (var parent = Process.GetProcessById(pid))
            {
                if (!string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected update target.");
                using var ready = EventWaitHandle.OpenExisting("Local\\BluetoothBatteryMonitor_UpdateReady_" + pid);
                ready.Set();
                if (!parent.WaitForExit(30000)) throw new IOException("The application did not exit in time.");
            }
            backup = target + "." + Guid.NewGuid().ToString("N") + ".bak";
            string replacement = backup + ".exe";
            try
            {
                File.Copy(staged, replacement);
                File.Replace(replacement, target, backup);
                replaced = true;
                var start = new ProcessStartInfo(target) { UseShellExecute = true };
                foreach (var argument in UpdateLaunchArguments.Completed(Application.ExecutablePath, reopenSettings))
                    start.ArgumentList.Add(argument);
                if (Process.Start(start) == null) throw new IOException("Could not restart the application.");
                Delete(backup);
            }
            catch
            {
                if (replaced) { File.Copy(backup, target, true); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                throw;
            }
            finally { Delete(replacement); Delete(staged); }
        }
        catch (Exception ex) { MessageBox.Show("Update failed: " + ex.Message, "Bluetooth Battery Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        return true;
    }
    internal static async Task CleanupHelperAsync(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("BluetoothBatteryMonitor-helper-", StringComparison.Ordinal)) return;
        for (int i = 0; i < 20 && File.Exists(path); i++) { Delete(path); await Task.Delay(500); }
    }
    public void Dispose() { _timer.Dispose(); Cancel(); Discard(); _http.Dispose(); }
}
