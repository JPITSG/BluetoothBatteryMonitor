using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BluetoothBatteryMonitor
{
    public class ConfigurationDialog : Form
    {
        private const int DialogClientWidth = 420;
        private const int InitialClientHeight = 180;
        private const int MinimumClientHeight = 130;
        private const int ScreenPadding = 64;

        private WebView2? _webView;
        private System.Drawing.Icon? _dialogIcon;
        private bool _initialized;
        private readonly CancellationTokenSource _lifetime = new();
        private static Task<CoreWebView2Environment>? _environment;
        private static List<string> _cachedPairedNames = new();
        private static readonly Lazy<string> HtmlContent = new(() =>
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BluetoothBatteryMonitor.config_ui.html")
                ?? throw new InvalidOperationException("The configuration page is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });

        public ConfigurationDialog()
        {
            Text = "Configuration";
            ClientSize = new System.Drawing.Size(DialogClientWidth, InitialClientHeight);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            Opacity = 0;
            // Load the small embedded icon instead of inspecting the bundled EXE
            // on the UI thread every time configuration opens.
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BluetoothBatteryMonitor.icon.ico"))
            {
                if (stream != null) Icon = _dialogIcon = new System.Drawing.Icon(stream);
            }

            AppUpdater.Instance.Changed += SendUpdateState;
            InitializeWebView();
        }

        private void InitializeWebView()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            _ = InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                var html = Task.Run(() => HtmlContent.Value);
                // Reuse the environment/profile between dialog openings. A failed
                // runtime startup may be retried on the next opening.
                if (_environment == null || _environment.IsFaulted || _environment.IsCanceled)
                {
                    var userDataFolder = Path.Combine(Path.GetTempPath(), "BluetoothBatteryMonitor", "WebView2");
                    _environment = CoreWebView2Environment.CreateAsync(null, userDataFolder);
                }
                var env = await _environment;
                if (_lifetime.IsCancellationRequested) return;
                await _webView!.EnsureCoreWebView2Async(env);
                var content = await html;
                if (_lifetime.IsCancellationRequested) return;

                var core = _webView.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigateToString(content);
            }
            catch (Exception ex)
            {
                if (IsDisposed || _lifetime.IsCancellationRequested) return;
                MessageBox.Show("Could not open configuration.\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                using var json = JsonDocument.Parse(raw);
                var action = json.RootElement.GetProperty("action").GetString();

                switch (action)
                {
                    case "getInit":
                        HandleGetInit();
                        break;

                    case "dismissUpdate":
                        AppUpdater.Instance.Dismiss();
                        break;
                    case "getUpdateState":
                        SendUpdateState();
                        break;
                    case "checkUpdate":
                        await AppUpdater.Instance.CheckAsync(false);
                        break;
                    case "cancelUpdate":
                        AppUpdater.Instance.Cancel();
                        break;
                    case "installUpdate":
                        await AppUpdater.Instance.InstallAsync();
                        break;
                    case "ignoreUpdate":
                        AppUpdater.Instance.Ignore();
                        break;
                    case "autoUpdate":
                        AppUpdater.Instance.AutoCheck = json.RootElement.GetProperty("enabled").GetBoolean();
                        break;

                    case "saveDevices":
                        HandleSaveDevices(json.RootElement);
                        break;

                    case "close":
                        DialogResult = DialogResult.Cancel;
                        Close();
                        break;

                    case "resize":
                        if (json.RootElement.TryGetProperty("height", out var heightProp))
                        {
                            ResizeToContent(heightProp.GetInt32());

                            if (Opacity == 0)
                            {
                                CenterToScreen();
                                Opacity = 1;
                            }
                        }
                        break;
                }
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ResizeClientArea(InitialClientHeight);
        }

        private void ResizeToContent(int contentHeight)
        {
            var bounds = Bounds;
            var workingArea = Screen.FromControl(this).WorkingArea;
            ResizeClientArea(Math.Max(contentHeight, MinimumClientHeight));
            if (Opacity > 0 && bounds.Size != Size)
            {
                // Discovery may grow the already-visible dialog. Keep its centre
                // stable and its buttons inside the current monitor's work area.
                Left = Math.Clamp(bounds.Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
                Top = Math.Clamp(bounds.Top + (bounds.Height - Height) / 2,
                    workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
            }
        }

        private void ResizeClientArea(int logicalClientHeight)
        {
            var clientSize = new System.Drawing.Size(
                ScaleLogicalPixels(DialogClientWidth),
                ScaleLogicalPixels(ClampLogicalClientHeight(logicalClientHeight))
            );

            if (ClientSize != clientSize) ClientSize = clientSize;
            MinimumSize = SizeFromClientSize(new System.Drawing.Size(
                clientSize.Width,
                ScaleLogicalPixels(MinimumClientHeight)
            ));
        }

        private int ClampLogicalClientHeight(int logicalClientHeight)
        {
            var scale = GetDpiScale();
            var workingArea = Screen.FromControl(this).WorkingArea;
            int verticalChrome = Math.Max(0, Height - ClientSize.Height);
            int maxDeviceClientHeight = workingArea.Height - verticalChrome - ScaleLogicalPixels(ScreenPadding);
            int maxLogicalClientHeight = (int)Math.Floor(maxDeviceClientHeight / scale);

            return Math.Max(MinimumClientHeight, Math.Min(logicalClientHeight, maxLogicalClientHeight));
        }

        private int ScaleLogicalPixels(int value)
        {
            return (int)Math.Ceiling(value * GetDpiScale());
        }

        private double GetDpiScale()
        {
            return DeviceDpi > 0 ? DeviceDpi / 96.0 : 1.0;
        }

        private void HandleGetInit()
        {
            if (_initialized) return;
            _initialized = true;
            var configuredNames = new HashSet<string>(BatteryMonitor.LoadDeviceNamesFromRegistry(), StringComparer.OrdinalIgnoreCase);
            // Show saved/cached devices immediately; discovery must not hold the
            // footer or the entire dialog behind a slow Bluetooth enumeration.
            SendMessage(new
            {
                type = "init", devices = BuildDeviceList(configuredNames, _cachedPairedNames),
                version = AppUpdater.DisplayVersion, autoCheck = AppUpdater.Instance.AutoCheck,
                loadingDevices = true
            });
            SendUpdateState();
            _ = RefreshDevicesAsync(configuredNames);
            if (!AppUpdater.Instance.Status.StartsWith("Successfully updated", StringComparison.Ordinal))
                _ = AppUpdater.Instance.CheckAsync(true);
        }

        private static object[] BuildDeviceList(HashSet<string> configuredNames, IEnumerable<string> pairedNames)
        {
            // Retain saved devices even if Windows temporarily omits them; saving
            // during a refresh must not silently stop monitoring a device.
            return configuredNames.Concat(pairedNames).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => (object)new { name, isConfigured = configuredNames.Contains(name) }).ToArray();
        }

        private async Task RefreshDevicesAsync(HashSet<string> configuredNames)
        {
            try
            {
                var names = await Task.Run(EnumeratePairedDevicesAsync)
                    .WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
                if (_lifetime.IsCancellationRequested) return;
                _cachedPairedNames = names;
                SendMessage(new { type = "devices", devices = BuildDeviceList(configuredNames, names), loadingDevices = false });
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch
            {
                SendMessage(new
                {
                    type = "devices", devices = BuildDeviceList(configuredNames, _cachedPairedNames), loadingDevices = false,
                    deviceError = "Could not refresh Bluetooth devices. Your saved selection is still available."
                });
            }
        }

        private void SendMessage(object message)
        {
            if (IsDisposed || _lifetime.IsCancellationRequested || _webView?.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }

        private void SendUpdateState()
        {
            if (IsDisposed || _webView?.CoreWebView2 == null) return;
            var updater = AppUpdater.Instance;
            SendMessage(new { type = "update", status = updater.Status, busy = updater.Busy, installing = updater.Installing,
                canInstall = updater.CanInstall, automatic = updater.AutomaticResult,
                currentVersion = AppUpdater.DisplayVersion, remoteVersion = updater.AvailableVersion });
        }

        private void HandleSaveDevices(JsonElement root)
        {
            try
            {
                var devices = root.GetProperty("devices")
                    .EnumerateArray()
                    .Select(el => el.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                BatteryMonitor.SaveDevicesToRegistry(devices);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch { }
        }

        private static async Task<List<string>> EnumeratePairedDevicesAsync()
        {
            // These independent WinRT queries can run together on a worker thread.
            var leDevices = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector()).AsTask();
            var classicDevices = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector()).AsTask();
            var results = await Task.WhenAll(leDevices, classicDevices).ConfigureAwait(false);
            return results.SelectMany(devices => devices).Select(device => device.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                _lifetime.Cancel();
                AppUpdater.Instance.Changed -= SendUpdateState;
                AppUpdater.Instance.Cancel();
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;

                    _webView.Dispose();
                    _webView = null;
                }
                Icon = null;
                _dialogIcon?.Dispose();
                _dialogIcon = null;
                _lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
