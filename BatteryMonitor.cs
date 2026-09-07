using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;

namespace BluetoothBatteryMonitor
{
    public class BatteryMonitor : ApplicationContext, IDisposable
    {
        #region Private Fields
        private readonly Dictionary<string, DeviceInfo> _devices;
        private readonly Dictionary<string, PersistentTrayIcon> _trayIcons;
        private PersistentTrayIcon? _accessIcon;
        private readonly Dictionary<string, Icon> _deviceCurrentIcons;
        private readonly Dictionary<string, ToolStripItem> _deviceMenuItems;
        private readonly Dictionary<string, ToolStripItem> _deviceLastUpdateMenuItems;
        private readonly System.Windows.Forms.Timer _uiRefreshTimer;
        private System.Windows.Forms.Timer? _startupConfigurationTimer;
        private readonly System.Threading.Timer _reconnectTimer;
        private readonly System.Threading.Timer _stateVerificationTimer;
        private readonly SemaphoreSlim _deviceLock;
        private readonly SynchronizationContext _syncContext;
        private readonly CancellationTokenSource _disposeCts;
        private DeviceWatcher? _deviceWatcher;
        private DeviceWatcher? _classicDeviceWatcher;
        private readonly TimeSpan _stateVerificationInterval = TimeSpan.FromSeconds(60);
        private int _stateVerificationRunning;
        private int _sessionRefreshPending;
        private readonly TimeSpan _sessionRefreshDebounce = TimeSpan.FromSeconds(1);
        private readonly TimeSpan _uiRefreshInterval = TimeSpan.FromSeconds(1);
        
        // Cache for HFP device instance IDs (device name -> instance ID)
        private readonly Dictionary<string, string> _hfpInstanceIdCache = new(StringComparer.OrdinalIgnoreCase);

        private int _lastScreenWidth;
        private int _lastScreenHeight;
        private float _lastDpiX;
        private float _lastDpiY;
        
        private Icon? _iconFull;
        private Icon? _iconGood;
        private Icon? _iconMedium;
        private Icon? _iconLow;
        private Icon? _iconEmpty;

        private const string RegistryKeyPath = @"SOFTWARE\JPIT\BluetoothBatteryMonitor";
        private const string RegistryDevicesValue = "Devices";
        internal const string ShowConfigurationEventName = @"Global\BluetoothBatteryMonitor_ShowConfig";

        private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");
        private const int NotifyIconMaxTextLength = 63;
        private static readonly string[] BatteryPropertyKeys =
        {
            "System.Devices.Aep.Bluetooth.Le.BatteryLevel",
            "System.Devices.Aep.BatteryLevel",
            "System.Devices.Aep.BatteryLifePercent",
            "System.Devices.BatteryLifePercent",
            "System.Devices.BatteryLevel",
            "System.Devices.BatteryLife"
        };
        // Only documented property names may be requested. Unsupported battery
        // aliases can cause Windows to reject the entire enumeration request.
        private static readonly string[] ConnectionProperties =
            { "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired" };
        private static readonly string[] RequestedDeviceProperties =
            ConnectionProperties.Append("System.Devices.BatteryLife").ToArray();

        // P/Invoke for proper DPI detection
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private const int LOGPIXELSX = 88;
        private const int LOGPIXELSY = 90;
        
        // CfgMgr32 API for reading PnP device properties (like PowerShell's Get-PnpDeviceProperty)
        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);
        
        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            int dnDevInst,
            ref DEVPROPKEY propertyKey,
            out int propertyType,
            IntPtr propertyBuffer,
            ref int propertyBufferSize,
            int ulFlags);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public int pid;
        }
        
        private const int CR_SUCCESS = 0;
        private const int CR_BUFFER_SMALL = 0x1A;
        private const int CM_LOCATE_DEVNODE_NORMAL = 0;
        private const int DEVPROP_TYPE_BYTE = 0x00000003;
        private const int DEVPROP_TYPE_INT32 = 0x00000006;
        private const int DEVPROP_TYPE_UINT32 = 0x00000007;
        
        // HFP Battery property key: {104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid 2
        private static readonly DEVPROPKEY DEVPKEY_Bluetooth_HfpBattery = new()
        {
            fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
            pid = 2
        };
        
        // For detecting session changes (RDP connect/disconnect)
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int WTS_CONSOLE_CONNECT = 0x1;
        private const int WTS_REMOTE_DISCONNECT = 0x4;
        private const int WTS_SESSION_UNLOCK = 0x8;
        
        [DllImport("wtsapi32.dll")]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);
        
        [DllImport("wtsapi32.dll")]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
        
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        
        // Hidden window for receiving session notifications
        private SessionNotificationWindow? _notificationWindow;

        // Cross-instance trigger so a second launch with the /configure switch
        // can ask the already-running instance to open its configuration dialog.
        private EventWaitHandle? _showConfigEvent;
        private RegisteredWaitHandle? _showConfigWaitHandle;
        private bool _configurationDialogOpen;
        #endregion

        #region Constructor and Initialization
        public BatteryMonitor(bool forceConfiguration = false)
        {
            _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _disposeCts = new CancellationTokenSource();

            _devices = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
            _trayIcons = new Dictionary<string, PersistentTrayIcon>();
            _deviceCurrentIcons = new Dictionary<string, Icon>();
            _deviceMenuItems = new Dictionary<string, ToolStripItem>();
            _deviceLastUpdateMenuItems = new Dictionary<string, ToolStripItem>();
            _deviceLock = new SemaphoreSlim(1, 1);

            CaptureCurrentDisplaySettings();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            LoadBatteryIcons();
            InitializeDevices();
            bool shouldShowConfigurationOnLaunch = forceConfiguration || _devices.Count == 0;
            CreateTrayIcons();

            _uiRefreshTimer = new System.Windows.Forms.Timer
            {
                Interval = (int)_uiRefreshInterval.TotalMilliseconds
            };
            _uiRefreshTimer.Tick += OnUiRefreshTimerTick;
            _uiRefreshTimer.Start();

            _notificationWindow = new SessionNotificationWindow(this);
            InitializeDeviceWatcher();

            _reconnectTimer = new System.Threading.Timer(
                async _ => await RetryDisconnectedDevicesAsync(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30)
            );

            _stateVerificationTimer = new System.Threading.Timer(
                async _ => await VerifyDeviceStatesAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );

            ScheduleStateVerification();

            InitializeConfigurationTrigger();
            _ = VerifyDeviceStatesAsync();

            if (shouldShowConfigurationOnLaunch)
            {
                ScheduleStartupConfigurationDialog();
            }
        }
        #endregion

        #region Session Change Handling
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.SessionUnlock:
                    ScheduleSessionReconnect();
                    break;
                    
                case SessionSwitchReason.RemoteConnect:
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        CaptureCurrentDisplaySettings();
                    });
                    break;
            }
        }

        internal void OnWtsSessionChange(int reason)
        {
            if (reason == WTS_CONSOLE_CONNECT || reason == WTS_REMOTE_DISCONNECT || reason == WTS_SESSION_UNLOCK)
            {
                ScheduleSessionReconnect();
            }
        }

        private void ScheduleSessionReconnect()
        {
            if (Interlocked.Exchange(ref _sessionRefreshPending, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(_sessionRefreshDebounce);
                Interlocked.Exchange(ref _sessionRefreshPending, 0);
                await HandleSessionReconnectAsync();
            });
        }

        private async Task HandleSessionReconnectAsync()
        {
            var uiRefreshComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _syncContext.Post(_ =>
            {
                try
                {
                    RefreshTrayIconsForDpiChange();
                    CaptureCurrentDisplaySettings();
                    RestartDeviceWatchers();
                }
                catch { }
                finally
                {
                    uiRefreshComplete.TrySetResult();
                }
            }, null);

            await uiRefreshComplete.Task;
            await VerifyDeviceStatesAsync();
        }

        private void RefreshTrayIconsForDpiChange()
        {
            var oldIconFull = _iconFull;
            var oldIconGood = _iconGood;
            var oldIconMedium = _iconMedium;
            var oldIconLow = _iconLow;
            var oldIconEmpty = _iconEmpty;

            LoadBatteryIcons();

            foreach (var deviceName in _devices.Keys.ToArray())
            {
                if (!_trayIcons.TryGetValue(deviceName, out var notifyIcon))
                    continue;

                if (!_devices.TryGetValue(deviceName, out var deviceInfo))
                    continue;

                try
                {
                    ApplyDeviceIconState(deviceName, deviceInfo, notifyIcon);
                    UpdateTrayIconText(deviceName, deviceInfo);
                    UpdateContextMenuItems(deviceName);
                }
                catch { }
            }

            RefreshTrayVisibility();
            try
            {
                oldIconFull?.Dispose();
                oldIconGood?.Dispose();
                oldIconMedium?.Dispose();
                oldIconLow?.Dispose();
                oldIconEmpty?.Dispose();
            }
            catch { }
        }

        private ContextMenuStrip CreateContextMenuForDevice(string deviceName, DeviceInfo deviceInfo)
        {
            var contextMenu = new ContextMenuStrip { AutoSize = true };
            
            var deviceMenuItem = contextMenu.Items.Add(deviceName, null, null);
            deviceMenuItem.Enabled = false;

            string statusText = GetStatusText(deviceInfo);

            var statusMenuItem = contextMenu.Items.Add(statusText, null, null);
            statusMenuItem.Enabled = false;
            _deviceMenuItems[deviceName] = statusMenuItem;

            var lastUpdatedItem = contextMenu.Items.Add(FormatLastUpdateText(deviceInfo), null, null);
            lastUpdatedItem.Enabled = false;
            lastUpdatedItem.Available = deviceInfo.IsConnectedForDisplay && deviceInfo.LastUpdate.HasValue;
            _deviceLastUpdateMenuItems[deviceName] = lastUpdatedItem;

            contextMenu.Opening += (_, _) => UpdateContextMenuItems(deviceName);
            contextMenu.Closed += (_, _) => UpdateDeviceIcon(deviceName);

            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Open Bluetooth Settings", null, OnOpenBluetoothSettings);
            contextMenu.Items.Add("Configuration", null, OnConfigureClick);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, OnExitClick);

            return contextMenu;
        }
        #endregion

        #region Display Settings Management
        private void CaptureCurrentDisplaySettings()
        {
            try
            {
                _lastScreenWidth = Screen.PrimaryScreen?.Bounds.Width ?? 0;
                _lastScreenHeight = Screen.PrimaryScreen?.Bounds.Height ?? 0;

                IntPtr hdc = GetDC(IntPtr.Zero);
                _lastDpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                _lastDpiY = GetDeviceCaps(hdc, LOGPIXELSY);
                ReleaseDC(IntPtr.Zero, hdc);
            }
            catch { }
        }

        private bool HasDisplaySettingsChanged()
        {
            try
            {
                int currentWidth = Screen.PrimaryScreen?.Bounds.Width ?? 0;
                int currentHeight = Screen.PrimaryScreen?.Bounds.Height ?? 0;

                IntPtr hdc = GetDC(IntPtr.Zero);
                float currentDpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                float currentDpiY = GetDeviceCaps(hdc, LOGPIXELSY);
                ReleaseDC(IntPtr.Zero, hdc);

                return currentWidth != _lastScreenWidth ||
                       currentHeight != _lastScreenHeight ||
                       Math.Abs(currentDpiX - _lastDpiX) > 0.1f ||
                       Math.Abs(currentDpiY - _lastDpiY) > 0.1f;
            }
            catch
            {
                return false;
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (HasDisplaySettingsChanged())
                {
                    await HandleSessionReconnectAsync();
                }
            });
        }
        #endregion

        #region Icon Management
        private void LoadBatteryIcons()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                _iconFull = LoadIconFromResource(assembly, "icon_battery_full.ico") ?? CreateFallbackIcon();
                _iconGood = LoadIconFromResource(assembly, "icon_battery_good.ico") ?? CreateFallbackIcon();
                _iconMedium = LoadIconFromResource(assembly, "icon_battery_medium.ico") ?? CreateFallbackIcon();
                _iconLow = LoadIconFromResource(assembly, "icon_battery_low.ico") ?? CreateFallbackIcon();
                _iconEmpty = LoadIconFromResource(assembly, "icon_battery_empty.ico") ?? CreateFallbackIcon();
            }
            catch
            {
                _iconEmpty = CreateFallbackIcon();
            }
        }

        private static Icon? LoadIconFromResource(System.Reflection.Assembly assembly, string resourceName)
        {
            try
            {
                var stream = assembly.GetManifestResourceStream($"BluetoothBatteryMonitor.{resourceName}")
                          ?? assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                    return new Icon(stream);
            }
            catch { }
            return null;
        }

        private Icon CreateFallbackIcon()
        {
            using var bitmap = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Gray);
            using var pen = new Pen(Color.Red, 2);
            g.DrawRectangle(pen, 0, 0, 15, 15);
            return Icon.FromHandle(bitmap.GetHicon());
        }

        private Icon GetBatteryIcon(int? percentage)
        {
            return percentage switch
            {
                null => _iconEmpty ?? CreateFallbackIcon(),
                >= 75 => _iconFull ?? CreateFallbackIcon(),
                >= 50 => _iconGood ?? CreateFallbackIcon(),
                >= 25 => _iconMedium ?? CreateFallbackIcon(),
                >= 10 => _iconLow ?? CreateFallbackIcon(),
                _ => _iconEmpty ?? CreateFallbackIcon()
            };
        }

        // Visibility is reconciled across all devices after updating their state.
        private void ApplyDeviceIconState(string deviceName, DeviceInfo deviceInfo, PersistentTrayIcon icon)
        {
            var batteryIcon = GetBatteryIcon(deviceInfo.IsConnectedForDisplay ? deviceInfo.BatteryLevel : null);
            _deviceCurrentIcons[deviceName] = batteryIcon;

            if (icon.Icon != batteryIcon)
                icon.Icon = batteryIcon;
        }
        #endregion

        #region Device Management
        private void InitializeDevices()
        {
            foreach (var name in LoadDeviceNamesFromRegistry())
            {
                _devices[name] = new DeviceInfo(name);
            }
        }

        public static string[] LoadDeviceNamesFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(RegistryDevicesValue) is string[] names)
                    return names.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            }
            catch { }
            return Array.Empty<string>();
        }

        public static void SaveDevicesToRegistry(IEnumerable<string> names)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            key.SetValue(RegistryDevicesValue, names.ToArray(), RegistryValueKind.MultiString);
        }
        #endregion

        #region Tray Icon Management
        private void CreateTrayIcons()
        {
            foreach (var deviceName in _devices.Keys)
            {
                var deviceInfo = _devices[deviceName];
                
                var notifyIcon = new PersistentTrayIcon(TrayIconIdentity.ForDevice(deviceName))
                {
                    Icon = GetBatteryIcon(deviceInfo.BatteryLevel),
                    Visible = false,
                    Text = BuildNotifyIconText(deviceName, deviceInfo)
                };

                notifyIcon.DoubleClick += OnOpenBluetoothSettings;
                notifyIcon.ContextMenuStrip = CreateContextMenuForDevice(deviceName, deviceInfo);

                _trayIcons[deviceName] = notifyIcon;
                _deviceCurrentIcons[deviceName] = notifyIcon.Icon;
            }
            RefreshTrayVisibility();
        }

        private void RefreshTrayVisibility()
        {
            var devices = _devices.Values.Select(d => new TrayDevice(
                d.Name, d.IsConnectedForDisplay, _trayIcons.TryGetValue(d.Name, out var icon) && icon.Visible)).ToArray();
            var visible = TrayVisibility.SelectVisible(devices);
            if (_accessIcon == null)
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Configuration", null, OnConfigureClick);
                menu.Items.Add("-");
                menu.Items.Add("Exit", null, OnExitClick);
                _accessIcon = new PersistentTrayIcon(TrayIconIdentity.Configuration) { Text = "Bluetooth Battery Monitor\nNo devices configured", ContextMenuStrip = menu };
                _accessIcon.DoubleClick += OnConfigureClick;
            }
            _accessIcon.Icon = GetBatteryIcon(null);
            // Show replacements first, so changing connections never removes
            // the last entry point to configuration from the Windows tray.
            if (devices.Length == 0) _accessIcon.Visible = true;
            foreach (var name in visible)
                if (_trayIcons.TryGetValue(name, out var icon)) icon.Visible = true;
            foreach (var pair in _trayIcons)
                if (!visible.Contains(pair.Key)) pair.Value.Visible = false;
            if (devices.Length > 0) _accessIcon.Visible = false;
        }

        private void OnOpenBluetoothSettings(object? sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:bluetooth",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void OnConfigureClick(object? sender, EventArgs e)
        {
            ShowConfigurationDialog();
        }

        private void ShowConfigurationDialog()
        {
            if (_configurationDialogOpen)
                return;

            _configurationDialogOpen = true;
            try
            {
                using var dialog = new ConfigurationDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    ReloadConfiguration();
                }
            }
            finally
            {
                _configurationDialogOpen = false;
            }
        }

        // Creates a named event so that launching the app again with the
        // /configure switch (which exits immediately due to the single-instance
        // mutex) can signal this running instance to open the dialog.
        private void InitializeConfigurationTrigger()
        {
            try
            {
                _showConfigEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowConfigurationEventName);
                _showConfigWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                    _showConfigEvent,
                    (_, _) => _syncContext.Post(_ => ShowConfigurationDialog(), null),
                    null,
                    Timeout.Infinite,
                    false);
            }
            catch { }
        }

        private void ScheduleStartupConfigurationDialog()
        {
            _startupConfigurationTimer = new System.Windows.Forms.Timer
            {
                Interval = 100
            };
            _startupConfigurationTimer.Tick += OnStartupConfigurationTimerTick;
            _startupConfigurationTimer.Start();
        }

        private void OnStartupConfigurationTimerTick(object? sender, EventArgs e)
        {
            if (_startupConfigurationTimer != null)
            {
                _startupConfigurationTimer.Stop();
                _startupConfigurationTimer.Tick -= OnStartupConfigurationTimerTick;
                _startupConfigurationTimer.Dispose();
                _startupConfigurationTimer = null;
            }

            ShowConfigurationDialog();
        }

        private void ReloadConfiguration()
        {
            StopDeviceWatchers();

            foreach (var icon in _trayIcons.Values.ToArray())
            {
                try
                {
                    icon.Visible = false;
                    icon.ContextMenuStrip?.Dispose();
                    icon.Dispose();
                }
                catch { }
            }
            _trayIcons.Clear();
            _deviceCurrentIcons.Clear();
            _deviceMenuItems.Clear();
            _deviceLastUpdateMenuItems.Clear();

            foreach (var device in _devices.Values)
            {
                ReleaseConnection(device);
            }
            _devices.Clear();
            _hfpInstanceIdCache.Clear();

            InitializeDevices();
            CreateTrayIcons();
            InitializeDeviceWatcher();
            _ = VerifyDeviceStatesAsync();
        }

        private void OnExitClick(object? sender, EventArgs e)
        {
            Application.Exit();
        }
        #endregion

        #region Device Watcher
        private void InitializeDeviceWatcher()
        {
            _deviceWatcher = StartDeviceWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            _classicDeviceWatcher = StartDeviceWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        }

        private DeviceWatcher? StartDeviceWatcher(string selector)
        {
            foreach (var properties in new[] { RequestedDeviceProperties, ConnectionProperties })
            {
                DeviceWatcher? watcher = null;
                try
                {
                    watcher = DeviceInformation.CreateWatcher(selector, properties, DeviceInformationKind.AssociationEndpoint);
                    watcher.Added += OnDeviceAdded;
                    watcher.Updated += OnDeviceUpdated;
                    watcher.Removed += OnDeviceRemoved;
                    watcher.EnumerationCompleted += OnEnumerationCompleted;
                    watcher.Stopped += OnWatcherStopped;
                    watcher.Start();
                    return watcher;
                }
                catch (Exception ex)
                {
                    LogMonitorError("Start device watcher", ex);
                    if (watcher != null)
                    {
                        watcher.Added -= OnDeviceAdded;
                        watcher.Updated -= OnDeviceUpdated;
                        watcher.Removed -= OnDeviceRemoved;
                        watcher.EnumerationCompleted -= OnEnumerationCompleted;
                        watcher.Stopped -= OnWatcherStopped;
                    }
                }
            }
            return null;
        }

        private static async Task<DeviceInformationCollection> FindPairedDevicesAsync(string selector)
        {
            try { return await DeviceInformation.FindAllAsync(selector, RequestedDeviceProperties, DeviceInformationKind.AssociationEndpoint); }
            catch (Exception ex)
            {
                LogMonitorError("Enumerate battery properties; retrying connection properties", ex);
                return await DeviceInformation.FindAllAsync(selector, ConnectionProperties, DeviceInformationKind.AssociationEndpoint);
            }
        }

        private static void LogMonitorError(string operation, Exception error)
        {
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothBatteryMonitor");
                System.IO.Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder, "diagnostics.log");
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256 * 1024)
                    System.IO.File.Move(path, path + ".old", true);
                System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:O} {operation}: {error.GetType().Name} (0x{error.HResult:X8}) {error.Message}\n");
            }
            catch { }
        }

        private void RestartDeviceWatchers()
        {
            if (_disposeCts.IsCancellationRequested)
                return;

            StopDeviceWatchers();
            InitializeDeviceWatcher();
        }

        // Watchers and WinRT events arrive on worker threads. Keep all device
        // state and tray decisions on one context, including async continuations.
        private Task RunOnUiAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _syncContext.Post(async _ =>
                {
                    try { if (!_disposeCts.IsCancellationRequested) await action(); }
                    catch (Exception ex) { LogMonitorError("Handle device event", ex); }
                    finally { completion.TrySetResult(); }
                }, null);
            }
            catch (InvalidOperationException) { completion.TrySetResult(); }
            catch (System.ComponentModel.InvalidAsynchronousStateException) { completion.TrySetResult(); }
            return completion.Task;
        }

        private bool IsCurrentWatcher(DeviceWatcher watcher) =>
            ReferenceEquals(watcher, _deviceWatcher) || ReferenceEquals(watcher, _classicDeviceWatcher);

        private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
        {
            await RunOnUiAsync(async () =>
            {
                if (IsCurrentWatcher(sender)) await ProcessDeviceAsync(args);
            });
        }

        private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            await RunOnUiAsync(async () =>
            {
                if (!IsCurrentWatcher(sender)) return;
                var deviceName = FindDeviceNameById(args.Id);
                if (deviceName == null)
                {
                    await VerifyDeviceStatesCoreAsync();
                    return;
                }
                // A disconnect can arrive in Updated without a Removed event,
                // and may contain a stale battery (including zero) in the same packet.
                if (args.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connected) && connected is false)
                {
                    HandleDeviceDisconnected(deviceName);
                    await VerifyDeviceStatesCoreAsync();
                    return;
                }
                var entry = _devices[deviceName];
                if (connected is bool windowsConnected) entry.WindowsConnected = windowsConnected;
                if (args.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var paired) && paired is bool isPaired)
                    entry.IsPaired = isPaired;
                if (!entry.IsConnected || !IsDeviceConnected(entry))
                {
                    HandleDeviceDisconnected(deviceName);
                    await VerifyDeviceStatesCoreAsync();
                    return;
                }
                foreach (var key in BatteryPropertyKeys)
                {
                    if (args.Properties.TryGetValue(key, out var batteryValue) &&
                        TryUpdateBatteryLevelFromValue(deviceName, batteryValue)) return;
                }
                if (entry.ConnectionType == DeviceConnectionType.BluetoothClassic)
                    await TryReadHfpBatteryViaCfgMgrAsync(deviceName);
            });
        }

        private async void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            await RunOnUiAsync(async () =>
            {
                if (!IsCurrentWatcher(sender)) return;
                var deviceName = FindDeviceNameById(args.Id);
                if (deviceName != null) HandleDeviceDisconnected(deviceName);
                await VerifyDeviceStatesCoreAsync();
            });
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args) { }
        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            if (sender.Status == DeviceWatcherStatus.Aborted)
                LogMonitorError("Device watcher aborted", new InvalidOperationException("Windows stopped device enumeration unexpectedly."));
        }

        private bool IsCurrentDevice(DeviceInfo entry, long generation) =>
            !_disposeCts.IsCancellationRequested && entry.Generation == generation &&
            _devices.TryGetValue(entry.Name, out var current) && ReferenceEquals(current, entry);

        private static bool IsDeviceConnected(DeviceInfo entry) => ConnectionEvidence.IsConnected(
            entry.IsPaired, entry.WindowsConnected, IsNativeConnected(entry));

        private static bool IsNativeConnected(DeviceInfo entry)
        {
            try
            {
                return entry.BluetoothDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected ||
                    entry.ClassicDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
            catch { return false; }
        }

        private static void ObserveWindowsState(DeviceInfo entry, DeviceInformation info)
        {
            entry.IsPaired = info.Pairing.IsPaired ||
                (info.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var paired) && paired is true);
            entry.WindowsConnected = info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connected)
                ? connected as bool? : null;
        }

        private async Task ProcessDeviceAsync(DeviceInformation deviceInfo)
        {
            if (!_devices.TryGetValue(deviceInfo.Name, out var entry)) return;
            long previousGeneration = entry.Generation;
            long generation = previousGeneration;
            bool initialized = false;
            await _deviceLock.WaitAsync(_disposeCts.Token);
            try
            {
                if (!IsCurrentDevice(entry, previousGeneration)) return;
                if (string.Equals(entry.DeviceId, deviceInfo.Id, StringComparison.OrdinalIgnoreCase))
                    ObserveWindowsState(entry, deviceInfo);
                if (entry.IsConnected && IsDeviceConnected(entry))
                {
                    if (string.Equals(entry.DeviceId, deviceInfo.Id, StringComparison.OrdinalIgnoreCase))
                        TryUpdateBatteryFromProperties(deviceInfo, entry.Name);
                    return;
                }
                HandleDeviceDisconnected(entry.Name);
                generation = entry.BeginConnection(deviceInfo.Id);
                ObserveWindowsState(entry, deviceInfo);
                if (entry.WindowsConnected == false) return;

                // Publish the paired endpoint's known state before opening a
                // Bluetooth object, which may wait for an idle device to respond.
                if (entry.ConfirmConnection(generation, IsDeviceConnected(entry)))
                {
                    TryUpdateBatteryFromProperties(deviceInfo, entry.Name);
                    UpdateDeviceIcon(entry.Name);
                }
                await OpenNativeDeviceAsync(entry, generation);
                if (!IsCurrentDevice(entry, generation)) return;

                // Use Windows' explicit connection flag for this paired endpoint.
                // Battery data and the mere existence of an object are not evidence.
                if (!entry.ConfirmConnection(generation, IsDeviceConnected(entry)))
                {
                    HandleDeviceDisconnected(entry.Name);
                    return;
                }
                TryUpdateBatteryFromProperties(deviceInfo, entry.Name);
                UpdateDeviceIcon(entry.Name);
                initialized = true;
            }
            catch (Exception ex) { LogMonitorError("Process device", ex); }
            finally { _deviceLock.Release(); }

            // A sleeping peripheral's battery IO must not block identifying
            // another device that Windows already reports as connected.
            if (!initialized || !IsCurrentDevice(entry, generation) || !entry.IsConnected) return;
            if (entry.ClassicDevice != null) await TryReadHfpBatteryViaCfgMgrAsync(entry.Name);
            else await ConnectToBatteryServiceAsync(entry, generation);
        }

        private async Task OpenNativeDeviceAsync(DeviceInfo entry, long generation)
        {
            if (!IsCurrentDevice(entry, generation) || entry.DeviceId == null ||
                entry.BluetoothDevice != null || entry.ClassicDevice != null || entry.NativeOpenGeneration == generation) return;
            entry.NativeOpenGeneration = generation;
            try
            {
                bool classic = entry.DeviceId.StartsWith("Bluetooth#", StringComparison.OrdinalIgnoreCase) &&
                    !entry.DeviceId.Contains("BluetoothLE", StringComparison.OrdinalIgnoreCase);
                if (classic)
                {
                    entry.ConnectionType = DeviceConnectionType.BluetoothClassic;
                    var device = await BluetoothDevice.FromIdAsync(entry.DeviceId);
                    if (!IsCurrentDevice(entry, generation)) { device?.Dispose(); return; }
                    entry.ClassicDevice = device;
                    if (device != null) device.ConnectionStatusChanged += OnClassicConnectionStatusChanged;
                }
                else
                {
                    entry.ConnectionType = DeviceConnectionType.BluetoothLe;
                    var device = await BluetoothLEDevice.FromIdAsync(entry.DeviceId);
                    if (!IsCurrentDevice(entry, generation)) { device?.Dispose(); return; }
                    entry.BluetoothDevice = device;
                    if (device != null) device.ConnectionStatusChanged += OnLeConnectionStatusChanged;
                }
            }
            catch (Exception ex) { LogMonitorError("Open Bluetooth device", ex); }
            finally { if (entry.NativeOpenGeneration == generation) entry.NativeOpenGeneration = null; }
        }

        private async void OnLeConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            await RunOnUiAsync(async () =>
            {
                var entry = _devices.Values.FirstOrDefault(d => ReferenceEquals(d.BluetoothDevice, sender));
                if (entry != null && !IsDeviceConnected(entry)) HandleDeviceDisconnected(entry.Name);
                await VerifyDeviceStatesCoreAsync();
            });
        }

        private async void OnClassicConnectionStatusChanged(BluetoothDevice sender, object args)
        {
            await RunOnUiAsync(async () =>
            {
                var entry = _devices.Values.FirstOrDefault(d => ReferenceEquals(d.ClassicDevice, sender));
                if (entry != null && !IsDeviceConnected(entry)) HandleDeviceDisconnected(entry.Name);
                await VerifyDeviceStatesCoreAsync();
            });
        }

        private async Task ConnectToBatteryServiceAsync(DeviceInfo entry, long generation)
        {
            if (!IsCurrentDevice(entry, generation) || !entry.IsConnected || entry.BluetoothDevice == null ||
                entry.BatteryInitializationGeneration == generation) return;
            entry.BatteryInitializationGeneration = generation;
            GattDeviceService? service = null;
            try
            {
                var result = await entry.BluetoothDevice.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Cached);
                service = result.Services.FirstOrDefault();
                foreach (var extra in result.Services.Skip(1)) extra.Dispose();
                if (!IsCurrentDevice(entry, generation) || !IsDeviceConnected(entry)) return;
                if (result.Status != GattCommunicationStatus.Success || service == null)
                {
                    service?.Dispose();
                    service = null;
                    result = await entry.BluetoothDevice.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached);
                    service = result.Services.FirstOrDefault();
                    foreach (var extra in result.Services.Skip(1)) extra.Dispose();
                }
                if (result.Status != GattCommunicationStatus.Success || service == null ||
                    !IsCurrentDevice(entry, generation) || !IsDeviceConnected(entry)) return;
                var chars = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Cached);
                if (!IsCurrentDevice(entry, generation) || !IsDeviceConnected(entry)) return;
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                    chars = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Uncached);
                if (chars.Status != GattCommunicationStatus.Success || !IsCurrentDevice(entry, generation) || !IsDeviceConnected(entry)) return;
                var characteristic = chars.Characteristics.FirstOrDefault();
                if (characteristic == null) return;
                entry.BatteryService = service;
                service = null; // Connection cleanup now owns this service.
                entry.BatteryCharacteristic = characteristic;
                characteristic.ValueChanged += OnBatteryLevelChanged;

                // Display Windows' existing value before waiting for the device
                // to answer. A cached value may only fill an unknown battery.
                var cached = await characteristic.ReadValueAsync(BluetoothCacheMode.Cached);
                if (cached.Status == GattCommunicationStatus.Success && cached.Value.Length > 0)
                    UpdateBatteryLevel(entry, generation, DataReader.FromBuffer(cached.Value).ReadByte(), cached: true);
                if (!IsCurrentDevice(entry, generation) || !IsDeviceConnected(entry)) return;
                var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status == GattCommunicationStatus.Success && read.Value.Length > 0)
                    UpdateBatteryLevel(entry, generation, DataReader.FromBuffer(read.Value).ReadByte());
                if (!IsCurrentDevice(entry, generation) || !IsDeviceConnected(entry)) return;
                if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                    await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                else if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            }
            catch { }
            finally
            {
                service?.Dispose();
                if (entry.BatteryInitializationGeneration == generation) entry.BatteryInitializationGeneration = null;
            }
        }

        private async void OnBatteryLevelChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            await RunOnUiAsync(() =>
            {
                var entry = _devices.Values.FirstOrDefault(d => ReferenceEquals(d.BatteryCharacteristic, sender));
                if (entry != null && args.CharacteristicValue.Length > 0)
                    UpdateBatteryLevel(entry, entry.Generation, DataReader.FromBuffer(args.CharacteristicValue).ReadByte());
                return Task.CompletedTask;
            });
        }

        private bool TryUpdateBatteryFromProperties(DeviceInformation deviceInfo, string deviceName)
        {
            foreach (var key in BatteryPropertyKeys)
            {
                if (deviceInfo.Properties.TryGetValue(key, out var batteryValue) &&
                    TryParseBatteryLevel(batteryValue, out var batteryLevel))
                {
                    if (_devices.TryGetValue(deviceName, out var entry))
                        UpdateBatteryLevel(entry, entry.Generation, batteryLevel, cached: true);
                    return true;
                }
            }
            return false;
        }

        private bool TryUpdateBatteryLevelFromValue(string deviceName, object? batteryValue)
        {
            if (batteryValue == null || !TryConvertBatteryLevel(batteryValue, out var batteryLevel)) return false;
            UpdateBatteryLevel(deviceName, batteryLevel);
            return true;
        }

        private static bool TryConvertBatteryLevel(object value, out byte batteryLevel)
        {
            batteryLevel = 0;
            try
            {
                int level = value switch
                {
                    byte b => b,
                    sbyte sb => sb,
                    short s => s,
                    ushort us => us,
                    int i => i,
                    uint ui => (int)ui,
                    long l => (int)l,
                    ulong ul => (int)ul,
                    float f => (int)f,
                    double d => (int)d,
                    string s => int.Parse(s.Replace("%", string.Empty).Trim(), CultureInfo.InvariantCulture),
                    _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
                };

                if (level is < 0 or > 100)
                    return false;

                batteryLevel = (byte)level;
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        private async Task<bool> TryReadHfpBatteryViaCfgMgrAsync(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var entry) || !entry.IsConnected || entry.ClassicDevice == null) return false;
            long generation = entry.Generation;
            string address = entry.ClassicDevice.BluetoothAddress.ToString("X12", CultureInfo.InvariantCulture);
            _hfpInstanceIdCache.TryGetValue(deviceName, out var cachedInstanceId);
            // Only registry/driver IO runs on a worker. Publish on the UI context
            // after checking that the same connection still owns the result.
            var result = await Task.Run<(byte? Battery, string? InstanceId)>(() =>
            {
                try
                {
                    if (cachedInstanceId != null && GetHfpBatteryLevel(cachedInstanceId) is byte cached)
                        return (cached, cachedInstanceId);
                    using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
                    if (enumKey == null) return (null, null);
                    foreach (var subKeyName in enumKey.GetSubKeyNames())
                    {
                        if (!subKeyName.Contains("111e", StringComparison.OrdinalIgnoreCase)) continue;
                        using var serviceKey = enumKey.OpenSubKey(subKeyName);
                        if (serviceKey == null) continue;
                        foreach (var instanceName in serviceKey.GetSubKeyNames())
                        {
                            // Match the connected radio address, not a substring
                            // of a friendly name shared by another paired device.
                            if (!instanceName.Contains(address, StringComparison.OrdinalIgnoreCase)) continue;
                            var instanceId = $"BTHENUM\\{subKeyName}\\{instanceName}";
                            if (GetHfpBatteryLevel(instanceId) is byte battery) return (battery, instanceId);
                        }
                    }
                }
                catch { }
                return (null, null);
            });
            if (!IsCurrentDevice(entry, generation)) return false;
            if (!IsDeviceConnected(entry)) { HandleDeviceDisconnected(deviceName); return false; }
            if (result.InstanceId == null) _hfpInstanceIdCache.Remove(deviceName);
            else _hfpInstanceIdCache[deviceName] = result.InstanceId;
            if (result.Battery is not byte level) return false;
            UpdateBatteryLevel(entry, generation, level);
            return true;
        }

        private byte? GetHfpBatteryLevel(string instanceId)
        {
            try
            {
                int result = CM_Locate_DevNodeW(out int devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
                if (result != CR_SUCCESS)
                    return null;
                
                int bufferSize = 0;
                var propKey = DEVPKEY_Bluetooth_HfpBattery;
                result = CM_Get_DevNode_PropertyW(devInst, ref propKey, out int propertyType, IntPtr.Zero, ref bufferSize, 0);
                
                if (result != CR_BUFFER_SMALL && result != CR_SUCCESS)
                    return null;
                
                if (bufferSize == 0)
                    return null;
                
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    result = CM_Get_DevNode_PropertyW(devInst, ref propKey, out propertyType, buffer, ref bufferSize, 0);
                    if (result != CR_SUCCESS)
                        return null;
                    
                    if (propertyType == DEVPROP_TYPE_BYTE && bufferSize >= 1)
                        return Marshal.ReadByte(buffer);
                    
                    if ((propertyType == DEVPROP_TYPE_INT32 || propertyType == DEVPROP_TYPE_UINT32) && bufferSize >= 4)
                    {
                        int value = Marshal.ReadInt32(buffer);
                        if (value >= 0 && value <= 100)
                            return (byte)value;
                    }
                    
                    if (bufferSize >= 1)
                    {
                        byte rawValue = Marshal.ReadByte(buffer);
                        if (rawValue <= 100)
                            return rawValue;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }
            
            return null;
        }

        private static bool TryParseBatteryLevel(object? batteryValue, out byte batteryLevel)
        {
            batteryLevel = 0;
            return batteryValue != null && TryConvertBatteryLevel(batteryValue, out batteryLevel);
        }

        private void UpdateBatteryLevel(string deviceName, byte batteryLevel)
        {
            if (_devices.TryGetValue(deviceName, out var entry))
                UpdateBatteryLevel(entry, entry.Generation, batteryLevel);
        }

        private void UpdateBatteryLevel(DeviceInfo entry, long generation, byte batteryLevel, bool cached = false)
        {
            if (!IsCurrentDevice(entry, generation)) return;
            if (!IsDeviceConnected(entry)) { HandleDeviceDisconnected(entry.Name); return; }
            bool updated = cached
                ? entry.TrySeedBattery(generation, batteryLevel)
                : entry.TryUpdateBattery(generation, batteryLevel, DateTime.Now);
            if (updated) UpdateDeviceIcon(entry.Name);
        }

        private void UpdateDeviceIcon(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var deviceInfo)) return;
            if (!_trayIcons.TryGetValue(deviceName, out var icon)) return;

            try
            {
                ApplyDeviceIconState(deviceName, deviceInfo, icon);

                UpdateTrayIconText(deviceName, deviceInfo);
                UpdateContextMenuItems(deviceName);
                RefreshTrayVisibility();
            }
            catch (ObjectDisposedException) { }
            catch { }
        }

        private void UpdateContextMenuItems(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var deviceInfo))
                return;

            try
            {
                if (_deviceMenuItems.TryGetValue(deviceName, out var statusItem) &&
                    !statusItem.IsDisposed &&
                    (statusItem.Owner == null || !statusItem.Owner.IsDisposed))
                {
                    statusItem.Text = GetStatusText(deviceInfo);
                }

                if (_deviceLastUpdateMenuItems.TryGetValue(deviceName, out var lastUpdateItem) &&
                    !lastUpdateItem.IsDisposed &&
                    (lastUpdateItem.Owner == null || !lastUpdateItem.Owner.IsDisposed))
                {
                    lastUpdateItem.Available = deviceInfo.IsConnectedForDisplay && deviceInfo.LastUpdate.HasValue;
                    lastUpdateItem.Text = FormatLastUpdateText(deviceInfo);
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void OnUiRefreshTimerTick(object? sender, EventArgs e)
        {
            if (_disposeCts.IsCancellationRequested)
                return;

            foreach (var deviceName in _devices.Keys.ToArray())
            {
                if (!_devices.TryGetValue(deviceName, out var deviceInfo))
                    continue;

                if (deviceInfo.IsConnected && !IsDeviceConnected(deviceInfo))
                    HandleDeviceDisconnected(deviceName);
                UpdateTrayIconText(deviceName, deviceInfo);
                UpdateContextMenuItems(deviceName);
            }
        }

        private void UpdateTrayIconText(string deviceName, DeviceInfo deviceInfo)
        {
            if (!_trayIcons.TryGetValue(deviceName, out var icon))
                return;

            try
            {
                string tooltipText = BuildNotifyIconText(deviceName, deviceInfo);
                if (!string.Equals(icon.Text, tooltipText, StringComparison.Ordinal))
                    icon.Text = tooltipText;
            }
            catch (ObjectDisposedException) { }
            catch (ArgumentOutOfRangeException) { }
        }

        private static string GetStatusText(DeviceInfo deviceInfo)
        {
            return deviceInfo.StatusText;
        }

        private static string BuildNotifyIconText(string deviceName, DeviceInfo deviceInfo)
        {
            string statusText = GetStatusText(deviceInfo);
            string tooltipText = deviceInfo.IsConnectedForDisplay && deviceInfo.LastUpdate.HasValue
                ? $"{deviceName}\n{statusText}\n{FormatLastUpdateShortText(deviceInfo)}"
                : $"{deviceName}\n{statusText}";
            return TruncateNotifyIconText(tooltipText);
        }

        private static string TruncateNotifyIconText(string tooltipText)
        {
            if (tooltipText.Length <= NotifyIconMaxTextLength)
                return tooltipText;

            var lines = tooltipText.Split('\n');
            if (lines.Length < 2)
                return tooltipText[..NotifyIconMaxTextLength];

            string tail = string.Join("\n", lines.Skip(1));
            int remaining = NotifyIconMaxTextLength - tail.Length - 1;
            if (remaining <= 0)
                return tooltipText[..NotifyIconMaxTextLength];

            string head = TruncateWithEllipsis(lines[0], remaining);
            string recomposed = $"{head}\n{tail}";
            return recomposed.Length <= NotifyIconMaxTextLength
                ? recomposed
                : recomposed[..NotifyIconMaxTextLength];
        }

        private static string TruncateWithEllipsis(string value, int maxLength)
        {
            if (value.Length <= maxLength)
                return value;

            if (maxLength <= 3)
                return value[..maxLength];

            return value[..(maxLength - 3)] + "...";
        }

        private static string FormatLastUpdateShortText(DeviceInfo deviceInfo)
        {
            if (!deviceInfo.LastUpdate.HasValue)
                return "Updated: --";

            var seconds = Math.Max(0, (int)Math.Floor((DateTime.Now - deviceInfo.LastUpdate.Value).TotalSeconds));
            return $"Updated: {seconds}s";
        }

        private static string FormatLastUpdateText(DeviceInfo deviceInfo)
        {
            if (!deviceInfo.LastUpdate.HasValue)
                return "Updated: --";

            var seconds = Math.Max(0, (int)Math.Floor((DateTime.Now - deviceInfo.LastUpdate.Value).TotalSeconds));
            return $"Updated: {seconds} seconds ago";
        }

        private void ReleaseConnection(DeviceInfo entry)
        {
            entry.Disconnect(); // Invalidate reads before disposing native handles.
            entry.WindowsConnected = null;
            entry.IsPaired = false;
            try
            {
                if (entry.BatteryCharacteristic != null)
                    entry.BatteryCharacteristic.ValueChanged -= OnBatteryLevelChanged;
            }
            catch { }
            entry.BatteryCharacteristic = null;
            entry.BatteryInitializationGeneration = null;
            entry.NativeOpenGeneration = null;
            try { entry.BatteryService?.Dispose(); } catch { }
            entry.BatteryService = null;
            if (entry.BluetoothDevice != null)
            {
                try { entry.BluetoothDevice.ConnectionStatusChanged -= OnLeConnectionStatusChanged; } catch { }
                try { entry.BluetoothDevice.Dispose(); } catch { }
                entry.BluetoothDevice = null;
            }
            if (entry.ClassicDevice != null)
            {
                try { entry.ClassicDevice.ConnectionStatusChanged -= OnClassicConnectionStatusChanged; } catch { }
                try { entry.ClassicDevice.Dispose(); } catch { }
                entry.ClassicDevice = null;
            }
            entry.ConnectionType = DeviceConnectionType.Unknown;
            _hfpInstanceIdCache.Remove(entry.Name);
        }

        private void HandleDeviceDisconnected(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var entry)) return;
            ReleaseConnection(entry);
            UpdateDeviceIcon(deviceName);
        }

        private string? FindDeviceNameById(string deviceId)
        {
            foreach (var kvp in _devices)
            {
                if (string.Equals(kvp.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    return kvp.Key;
            }
            return null;
        }

        private void ScheduleStateVerification()
        {
            if (_disposeCts.IsCancellationRequested)
                return;

            try
            {
                _stateVerificationTimer.Change(_stateVerificationInterval, _stateVerificationInterval);
            }
            catch (ObjectDisposedException) { }
        }

        private Task VerifyDeviceStatesAsync() => RunOnUiAsync(VerifyDeviceStatesCoreAsync);

        private async Task VerifyDeviceStatesCoreAsync()
        {
            if (Interlocked.CompareExchange(ref _stateVerificationRunning, 1, 0) == 1) return;
            try
            {
                var snapshot = _devices.Values.Select(entry => (Entry: entry, entry.Generation)).ToArray();
                var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var le = await FindPairedDevicesAsync(selector);
                var classic = await FindPairedDevicesAsync(classicSelector);
                var candidates = le.Concat(classic).ToLookup(d => d.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var item in snapshot)
                {
                    var entry = item.Entry;
                    // A watcher event or configuration change during enumeration
                    // takes precedence over this older enumeration result.
                    if (!IsCurrentDevice(entry, item.Generation)) continue;
                    var currentEndpoint = candidates[entry.Name].FirstOrDefault(d =>
                        string.Equals(d.Id, entry.DeviceId, StringComparison.OrdinalIgnoreCase));
                    if (currentEndpoint != null) ObserveWindowsState(entry, currentEndpoint);
                    else if (entry.DeviceId != null) entry.WindowsConnected = false;
                    if (entry.IsConnected && IsDeviceConnected(entry))
                    {
                        if (currentEndpoint != null) TryUpdateBatteryFromProperties(currentEndpoint, entry.Name);
                        await OpenNativeDeviceAsync(entry, item.Generation);
                        if (!IsCurrentDevice(entry, item.Generation) || !IsDeviceConnected(entry)) continue;
                        if (entry.ClassicDevice != null) await TryReadHfpBatteryViaCfgMgrAsync(entry.Name);
                        else if (entry.BatteryCharacteristic == null)
                            await ConnectToBatteryServiceAsync(entry, entry.Generation);
                        continue;
                    }
                    HandleDeviceDisconnected(entry.Name);
                    foreach (var candidate in candidates[entry.Name])
                    {
                        if (!_devices.TryGetValue(entry.Name, out var current) || !ReferenceEquals(entry, current)) break;
                        await ProcessDeviceAsync(candidate);
                        if (entry.IsConnected) break;
                    }
                }
            }
            catch (Exception ex) { LogMonitorError("Verify device states", ex); }
            finally { Interlocked.Exchange(ref _stateVerificationRunning, 0); }
        }

        private async Task RetryDisconnectedDevicesAsync()
        {
            await VerifyDeviceStatesAsync();
        }
        #endregion

        #region Disposal
        private void StopDeviceWatchers()
        {
            if (_deviceWatcher != null)
            {
                if (_deviceWatcher.Status == DeviceWatcherStatus.Started ||
                    _deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    _deviceWatcher.Stop();

                _deviceWatcher.Added -= OnDeviceAdded;
                _deviceWatcher.Updated -= OnDeviceUpdated;
                _deviceWatcher.Removed -= OnDeviceRemoved;
                _deviceWatcher.EnumerationCompleted -= OnEnumerationCompleted;
                _deviceWatcher.Stopped -= OnWatcherStopped;
                _deviceWatcher = null;
            }

            if (_classicDeviceWatcher != null)
            {
                if (_classicDeviceWatcher.Status == DeviceWatcherStatus.Started ||
                    _classicDeviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    _classicDeviceWatcher.Stop();

                _classicDeviceWatcher.Added -= OnDeviceAdded;
                _classicDeviceWatcher.Updated -= OnDeviceUpdated;
                _classicDeviceWatcher.Removed -= OnDeviceRemoved;
                _classicDeviceWatcher.EnumerationCompleted -= OnEnumerationCompleted;
                _classicDeviceWatcher.Stopped -= OnWatcherStopped;
                _classicDeviceWatcher = null;
            }
        }

        public new void Dispose()
        {
            try
            {
                _disposeCts.Cancel();

                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;

                _notificationWindow?.Dispose();

                _showConfigWaitHandle?.Unregister(null);
                _showConfigEvent?.Dispose();

                StopDeviceWatchers();

                if (_startupConfigurationTimer != null)
                {
                    _startupConfigurationTimer.Stop();
                    _startupConfigurationTimer.Tick -= OnStartupConfigurationTimerTick;
                    _startupConfigurationTimer.Dispose();
                    _startupConfigurationTimer = null;
                }

                foreach (var device in _devices.Values) ReleaseConnection(device);
                if (_accessIcon != null)
                {
                    _accessIcon.Visible = false;
                    _accessIcon.ContextMenuStrip?.Dispose();
                    _accessIcon.Dispose();
                    _accessIcon = null;
                }

                _uiRefreshTimer.Stop();
                _uiRefreshTimer.Tick -= OnUiRefreshTimerTick;
                _uiRefreshTimer.Dispose();

                _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _stateVerificationTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Task.Delay(50).Wait();

                _reconnectTimer.Dispose();
                _stateVerificationTimer.Dispose();

                foreach (var icon in _trayIcons.Values.ToArray())
                {
                    try
                    {
                        icon.Visible = false;
                        icon.ContextMenuStrip?.Dispose();
                        icon.Dispose();
                    }
                    catch { }
                }

                _trayIcons.Clear();
                _deviceCurrentIcons.Clear();
                _deviceMenuItems.Clear();
                _deviceLastUpdateMenuItems.Clear();

                _iconFull?.Dispose();
                _iconGood?.Dispose();
                _iconMedium?.Dispose();
                _iconLow?.Dispose();
                _iconEmpty?.Dispose();

                _deviceLock.Dispose();
                _disposeCts.Dispose();
                
                base.Dispose();
            }
            catch { }
        }
        #endregion

        #region DeviceInfo Class
        private class DeviceInfo : MonitorDeviceState
        {
            public DeviceInfo(string name) : base(name) { }
            public bool? WindowsConnected { get; set; }
            public bool IsPaired { get; set; }
            public BluetoothLEDevice? BluetoothDevice { get; set; }
            public BluetoothDevice? ClassicDevice { get; set; }
            public GattDeviceService? BatteryService { get; set; }
            public long? BatteryInitializationGeneration { get; set; }
            public long? NativeOpenGeneration { get; set; }
            public GattCharacteristic? BatteryCharacteristic { get; set; }
            public DeviceConnectionType ConnectionType { get; set; }
        }
        #endregion

        private enum DeviceConnectionType
        {
            Unknown = 0,
            BluetoothLe = 1,
            BluetoothClassic = 2
        }

        #region Session Notification Window
        private class SessionNotificationWindow : NativeWindow, IDisposable
        {
            private readonly BatteryMonitor _parent;
            private bool _disposed;

            public SessionNotificationWindow(BatteryMonitor parent)
            {
                _parent = parent;
                
                var cp = new CreateParams
                {
                    Caption = "BatteryMonitorSessionNotify",
                    Style = 0,
                    ExStyle = 0,
                    X = 0, Y = 0,
                    Width = 0, Height = 0,
                    Parent = IntPtr.Zero
                };
                
                CreateHandle(cp);
                WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_WTSSESSION_CHANGE)
                {
                    _parent.OnWtsSessionChange(m.WParam.ToInt32());
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (Handle != IntPtr.Zero)
                    {
                        WTSUnRegisterSessionNotification(Handle);
                        DestroyHandle();
                    }
                }
            }
        }
        #endregion
    }
}
