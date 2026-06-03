using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
        private string? _htmlContent;

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
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            LoadEmbeddedHtml();
            InitializeWebView();
        }

        private void LoadEmbeddedHtml()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("BluetoothBatteryMonitor.config_ui.html");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    _htmlContent = reader.ReadToEnd();
                }
            }
            catch { }
        }

        private void InitializeWebView()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            _webView.CoreWebView2InitializationCompleted += OnWebViewReady;
            _ = InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                var userDataFolder = Path.Combine(Path.GetTempPath(), "BluetoothBatteryMonitor", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView!.EnsureCoreWebView2Async(env);
            }
            catch { }
        }

        private void OnWebViewReady(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess || _webView?.CoreWebView2 == null) return;

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            if (_htmlContent != null)
            {
                _webView.CoreWebView2.NavigateToString(_htmlContent);
            }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                var json = JsonDocument.Parse(raw);
                var action = json.RootElement.GetProperty("action").GetString();

                switch (action)
                {
                    case "getInit":
                        await HandleGetInitAsync();
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
            ResizeClientArea(Math.Max(contentHeight, MinimumClientHeight));
        }

        private void ResizeClientArea(int logicalClientHeight)
        {
            var clientSize = new System.Drawing.Size(
                ScaleLogicalPixels(DialogClientWidth),
                ScaleLogicalPixels(ClampLogicalClientHeight(logicalClientHeight))
            );

            ClientSize = clientSize;
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

        private async Task HandleGetInitAsync()
        {
            try
            {
                var configuredNames = new HashSet<string>(
                    BatteryMonitor.LoadDeviceNamesFromRegistry(),
                    StringComparer.OrdinalIgnoreCase
                );

                var pairedNames = await EnumeratePairedDevicesAsync();

                var devices = pairedNames
                    .Select(name => new { name, isConfigured = configuredNames.Contains(name) })
                    .OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var initJson = JsonSerializer.Serialize(new { devices });
                await _webView!.CoreWebView2.ExecuteScriptAsync($"window.onInit({initJson})");
            }
            catch { }
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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var leSelector = BluetoothLEDevice.GetDeviceSelector();
                var classicSelector = BluetoothDevice.GetDeviceSelector();
                var leDevices = await DeviceInformation.FindAllAsync(leSelector);
                var classicDevices = await DeviceInformation.FindAllAsync(classicSelector);

                foreach (var device in leDevices.Concat(classicDevices))
                {
                    var name = device.Name?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }
            catch { }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;

                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
