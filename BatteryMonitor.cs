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
        private readonly Dictionary<string, NotifyIcon> _trayIcons;
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

        // Sentinel icon shown when no device icons are visible
        private NotifyIcon? _sentinelIcon;
        
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

        private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");
        private const int NotifyIconMaxTextLength = 63;
        private static readonly string[] BatteryPropertyKeys =
        {
            "System.Devices.Aep.Bluetooth.Le.BatteryLevel",
            "System.Devices.Aep.BatteryLevel",
            "System.Devices.Aep.BatteryLifePercent",
            "System.Devices.BatteryLifePercent",
            "System.Devices.BatteryLevel"
        };

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
        #endregion

        #region Constructor and Initialization
        public BatteryMonitor()
        {
            _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _disposeCts = new CancellationTokenSource();

            _devices = new Dictionary<string, DeviceInfo>();
            _trayIcons = new Dictionary<string, NotifyIcon>();
            _deviceCurrentIcons = new Dictionary<string, Icon>();
            _deviceMenuItems = new Dictionary<string, ToolStripItem>();
            _deviceLastUpdateMenuItems = new Dictionary<string, ToolStripItem>();
            _deviceLock = new SemaphoreSlim(1, 1);

            CaptureCurrentDisplaySettings();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            LoadBatteryIcons();
            InitializeDevices();
            bool shouldShowConfigurationOnLaunch = _devices.Count == 0;
            CreateTrayIcons();
            CreateSentinelIcon();
            UpdateSentinelVisibility();

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
            _syncContext.Post(_ =>
            {
                try
                {
                    RefreshTrayIconsForDpiChange();
                    CaptureCurrentDisplaySettings();
                }
                catch { }
            }, null);
            
            await Task.CompletedTask.ConfigureAwait(false);
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
                    var batteryIcon = GetBatteryIcon(deviceInfo.BatteryLevel);
                    _deviceCurrentIcons[deviceName] = batteryIcon;
                    notifyIcon.Icon = batteryIcon;
                }
                catch { }
            }

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
        #endregion

        #region Device Management
        private void InitializeDevices()
        {
            foreach (var name in LoadDeviceNamesFromRegistry())
            {
                _devices[name] = new DeviceInfo { Name = name };
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
                
                var notifyIcon = new NotifyIcon
                {
                    Icon = GetBatteryIcon(null),
                    Visible = true,
                    Text = $"{deviceName}\nScanning..."
                };

                notifyIcon.DoubleClick += OnOpenBluetoothSettings;
                notifyIcon.ContextMenuStrip = CreateContextMenuForDevice(deviceName, deviceInfo);

                _trayIcons[deviceName] = notifyIcon;
                _deviceCurrentIcons[deviceName] = notifyIcon.Icon;
            }
        }

        private void CreateSentinelIcon()
        {
            _sentinelIcon = new NotifyIcon
            {
                Icon = _iconEmpty ?? CreateFallbackIcon(),
                Text = "Bluetooth Battery Monitor\nNo devices"
            };
            _sentinelIcon.DoubleClick += OnConfigureClick;

            var contextMenu = new ContextMenuStrip { AutoSize = true };
            contextMenu.Items.Add("Configuration", null, OnConfigureClick);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, OnExitClick);
            _sentinelIcon.ContextMenuStrip = contextMenu;
        }

        private void UpdateSentinelVisibility()
        {
            if (_sentinelIcon == null) return;

            _sentinelIcon.Visible = _trayIcons.Count == 0;
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
            using var dialog = new ConfigurationDialog();
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                ReloadConfiguration();
            }
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
                try { device.BluetoothDevice?.Dispose(); } catch { }
            }
            _devices.Clear();
            _hfpInstanceIdCache.Clear();

            InitializeDevices();
            CreateTrayIcons();
            UpdateSentinelVisibility();
            InitializeDeviceWatcher();
        }

        private void OnExitClick(object? sender, EventArgs e)
        {
            Application.Exit();
        }
        #endregion

        #region Device Watcher
        private void InitializeDeviceWatcher()
        {
            try
            {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                
                _deviceWatcher = DeviceInformation.CreateWatcher(
                    selector,
                    new[] { "System.Devices.Aep.IsConnected" },
                    DeviceInformationKind.AssociationEndpoint
                );

                _deviceWatcher.Added += OnDeviceAdded;
                _deviceWatcher.Updated += OnDeviceUpdated;
                _deviceWatcher.Removed += OnDeviceRemoved;
                _deviceWatcher.EnumerationCompleted += OnEnumerationCompleted;
                _deviceWatcher.Stopped += OnWatcherStopped;
                _deviceWatcher.Start();

                string classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                _classicDeviceWatcher = DeviceInformation.CreateWatcher(
                    classicSelector,
                    new[] { "System.Devices.Aep.IsConnected" },
                    DeviceInformationKind.AssociationEndpoint
                );

                _classicDeviceWatcher.Added += OnDeviceAdded;
                _classicDeviceWatcher.Updated += OnDeviceUpdated;
                _classicDeviceWatcher.Removed += OnDeviceRemoved;
                _classicDeviceWatcher.EnumerationCompleted += OnEnumerationCompleted;
                _classicDeviceWatcher.Stopped += OnWatcherStopped;
                _classicDeviceWatcher.Start();
            }
            catch { }
        }

        private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
        {
            await ProcessDeviceAsync(args);
        }

        private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            var deviceName = FindDeviceNameById(args.Id);
            if (deviceName == null)
                return;

            foreach (var key in BatteryPropertyKeys)
            {
                if (args.Properties.TryGetValue(key, out var batteryValue))
                {
                    TryUpdateBatteryLevelFromValue(deviceName, batteryValue);
                    return;
                }
            }

            if (_devices.TryGetValue(deviceName, out var deviceInfo) &&
                deviceInfo.ConnectionType == DeviceConnectionType.BluetoothClassic)
            {
                await TryReadHfpBatteryViaCfgMgrAsync(deviceName).ConfigureAwait(false);
            }
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            var deviceName = FindDeviceNameById(args.Id);
            if (deviceName != null)
            {
                HandleDeviceDisconnected(deviceName);
            }
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args) { }
        private void OnWatcherStopped(DeviceWatcher sender, object args) { }

        private async Task ProcessDeviceAsync(DeviceInformation deviceInfo)
        {
            try
            {
                var deviceName = deviceInfo.Name;
                if (string.IsNullOrEmpty(deviceName) || !_devices.ContainsKey(deviceName))
                    return;

                await _deviceLock.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                try
                {
                    var entry = _devices[deviceName];
                    entry.DeviceId = deviceInfo.Id;
                    entry.IsConnected = true;
                    
                    TryUpdateBatteryFromProperties(deviceInfo, deviceName);
                    ScheduleStateVerification();

                    _syncContext.Post(_ => UpdateDeviceIcon(deviceName), null);

                    // Check if this looks like a classic Bluetooth device ID
                    bool looksLikeClassic = deviceInfo.Id.StartsWith("Bluetooth#", StringComparison.OrdinalIgnoreCase) &&
                                           !deviceInfo.Id.Contains("BluetoothLE", StringComparison.OrdinalIgnoreCase);
                    
                    // For classic-looking devices, try CfgMgr32 (it's fast!)
                    if (looksLikeClassic)
                    {
                        if (await TryReadHfpBatteryViaCfgMgrAsync(deviceName).ConfigureAwait(false))
                        {
                            entry.ConnectionType = DeviceConnectionType.BluetoothClassic;
                            return;
                        }
                    }

                    // Try BLE device creation
                    BluetoothLEDevice? leDevice = null;
                    try
                    {
                        leDevice = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                    }
                    catch { }
                    
                    if (leDevice != null)
                    {
                        entry.BluetoothDevice = leDevice;
                        entry.ConnectionType = DeviceConnectionType.BluetoothLe;
                        await ConnectToBatteryServiceAsync(leDevice, deviceName).ConfigureAwait(false);
                        return;
                    }

                    // It's classic Bluetooth
                    entry.ConnectionType = DeviceConnectionType.BluetoothClassic;
                    
                    // If we didn't get battery from CfgMgr32 earlier, try again
                    if (!entry.BatteryLevel.HasValue)
                    {
                        await TryReadHfpBatteryViaCfgMgrAsync(deviceName).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _deviceLock.Release();
                }
            }
            catch { }
        }

        private async Task ConnectToBatteryServiceAsync(BluetoothLEDevice device, string deviceName)
        {
            try
            {
                var gattResult = await device.GetGattServicesForUuidAsync(BatteryServiceUuid).AsTask().ConfigureAwait(false);
                if (gattResult.Status != GattCommunicationStatus.Success || gattResult.Services.Count == 0)
                    return;

                var batteryService = gattResult.Services[0];
                var charResult = await batteryService.GetCharacteristicsForUuidAsync(BatteryLevelUuid).AsTask().ConfigureAwait(false);
                
                if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                    return;

                var characteristic = charResult.Characteristics[0];
                _devices[deviceName].BatteryCharacteristic = characteristic;

                await ReadBatteryLevelAsync(characteristic, deviceName).ConfigureAwait(false);
                await SubscribeToBatteryNotificationsAsync(characteristic, deviceName).ConfigureAwait(false);
            }
            catch { }
        }

        private async Task SubscribeToBatteryNotificationsAsync(GattCharacteristic characteristic, string deviceName)
        {
            try
            {
                var notifyResult = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify
                ).AsTask().ConfigureAwait(false);

                if (notifyResult == GattCommunicationStatus.Success)
                {
                    characteristic.ValueChanged += (sender, args) => OnBatteryLevelChanged(sender, args, deviceName);
                }
            }
            catch { }
        }

        private async Task ReadBatteryLevelAsync(GattCharacteristic characteristic, string deviceName)
        {
            try
            {
                var readResult = await characteristic.ReadValueAsync().AsTask(_disposeCts.Token).ConfigureAwait(false);
                
                if (readResult.Status == GattCommunicationStatus.Success)
                {
                    var reader = DataReader.FromBuffer(readResult.Value);
                    byte batteryLevel = reader.ReadByte();
                    UpdateBatteryLevel(deviceName, batteryLevel);
                }
            }
            catch { }
        }

        private void OnBatteryLevelChanged(GattCharacteristic sender, GattValueChangedEventArgs args, string deviceName)
        {
            try
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                byte batteryLevel = reader.ReadByte();
                UpdateBatteryLevel(deviceName, batteryLevel);
            }
            catch { }
        }

        private bool TryUpdateBatteryFromProperties(DeviceInformation deviceInfo, string deviceName)
        {
            foreach (var key in BatteryPropertyKeys)
            {
                if (deviceInfo.Properties.TryGetValue(key, out var batteryValue) &&
                    TryParseBatteryLevel(batteryValue, out var batteryLevel))
                {
                    UpdateBatteryLevel(deviceName, batteryLevel);
                    return true;
                }
            }
            return false;
        }

        private void TryUpdateBatteryLevelFromValue(string deviceName, object? batteryValue)
        {
            if (batteryValue != null && TryConvertBatteryLevel(batteryValue, out var batteryLevel))
            {
                UpdateBatteryLevel(deviceName, batteryLevel);
            }
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
            return await Task.Run(() =>
            {
                try
                {
                    // Check cache first for fast path
                    if (_hfpInstanceIdCache.TryGetValue(deviceName, out var cachedInstanceId))
                    {
                        var battery = GetHfpBatteryLevel(cachedInstanceId);
                        if (battery.HasValue)
                        {
                            UpdateBatteryLevel(deviceName, battery.Value);
                            return true;
                        }
                        // Cache miss - device may have reconnected with new instance, clear and re-scan
                        _hfpInstanceIdCache.Remove(deviceName);
                    }
                    
                    using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
                    if (enumKey == null)
                        return false;
                    
                    foreach (var subKeyName in enumKey.GetSubKeyNames())
                    {
                        if (!subKeyName.Contains("111e", StringComparison.OrdinalIgnoreCase) &&
                            !subKeyName.Contains("111E", StringComparison.OrdinalIgnoreCase))
                            continue;
                        
                        using var serviceKey = enumKey.OpenSubKey(subKeyName);
                        if (serviceKey == null) continue;
                        
                        foreach (var instanceName in serviceKey.GetSubKeyNames())
                        {
                            using var instanceKey = serviceKey.OpenSubKey(instanceName);
                            if (instanceKey == null) continue;
                            
                            var friendlyName = instanceKey.GetValue("FriendlyName") as string;
                            if (string.IsNullOrEmpty(friendlyName) || 
                                !friendlyName.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            
                            var instanceId = $"BTHENUM\\{subKeyName}\\{instanceName}";
                            var battery = GetHfpBatteryLevel(instanceId);
                            if (battery.HasValue)
                            {
                                // Cache for future lookups
                                _hfpInstanceIdCache[deviceName] = instanceId;
                                UpdateBatteryLevel(deviceName, battery.Value);
                                return true;
                            }
                        }
                    }
                    
                    return false;
                }
                catch
                {
                    return false;
                }
            }).ConfigureAwait(false);
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
            if (_devices.TryGetValue(deviceName, out var device))
            {
                device.BatteryLevel = batteryLevel;
                device.LastUpdate = DateTime.Now;
                ScheduleStateVerification();
                _syncContext.Post(_ => UpdateDeviceIcon(deviceName), null);
            }
        }

        private void UpdateDeviceIcon(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var deviceInfo)) return;
            if (!_trayIcons.TryGetValue(deviceName, out var icon)) return;

            try
            {
                var batteryIcon = GetBatteryIcon(deviceInfo.IsConnected ? deviceInfo.BatteryLevel : null);
                _deviceCurrentIcons[deviceName] = batteryIcon;

                if (icon.Icon != batteryIcon)
                    icon.Icon = batteryIcon;

                UpdateTrayIconText(deviceName, deviceInfo);
                UpdateContextMenuItems(deviceName);
                UpdateSentinelVisibility();
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
            if (deviceInfo.IsConnected)
                return deviceInfo.BatteryLevel.HasValue ? $"Battery: {deviceInfo.BatteryLevel}%" : "Connected";

            return string.IsNullOrEmpty(deviceInfo.DeviceId) ? "Scanning..." : "Disconnected";
        }

        private static string BuildNotifyIconText(string deviceName, DeviceInfo deviceInfo)
        {
            string statusText = GetStatusText(deviceInfo);
            string updateText = FormatLastUpdateShortText(deviceInfo);
            string tooltipText = $"{deviceName}\n{statusText}\n{updateText}";
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

        private void HandleDeviceDisconnected(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var deviceInfo) || !deviceInfo.IsConnected)
                return;

            deviceInfo.IsConnected = false;
            deviceInfo.BatteryLevel = null;
            deviceInfo.LastUpdate = null;
            deviceInfo.BatteryCharacteristic = null;
            deviceInfo.ConnectionType = DeviceConnectionType.Unknown;

            try { deviceInfo.BluetoothDevice?.Dispose(); } catch { }
            deviceInfo.BluetoothDevice = null;
            
            // Clear cached HFP instance ID on disconnect
            _hfpInstanceIdCache.Remove(deviceName);

            _syncContext.Post(_ => UpdateDeviceIcon(deviceName), null);
            ScheduleStateVerification();
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

        private async Task VerifyDeviceStatesAsync()
        {
            if (Interlocked.CompareExchange(ref _stateVerificationRunning, 1, 0) == 1)
                return;

            try
            {
                var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                var classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                var connectedDevices = await DeviceInformation.FindAllAsync(selector).AsTask().ConfigureAwait(false);
                var classicDevices = await DeviceInformation.FindAllAsync(classicSelector).AsTask().ConfigureAwait(false);
                var connectedByName = new Dictionary<string, DeviceInformation>(StringComparer.OrdinalIgnoreCase);

                foreach (var device in connectedDevices)
                {
                    if (!string.IsNullOrWhiteSpace(device.Name))
                        connectedByName[device.Name] = device;
                }

                foreach (var device in classicDevices)
                {
                    if (!string.IsNullOrWhiteSpace(device.Name) && !connectedByName.ContainsKey(device.Name))
                        connectedByName[device.Name] = device;
                }

                foreach (var kvp in _devices.ToArray())
                {
                    var deviceName = kvp.Key;
                    bool isCurrentlyConnected = kvp.Value.IsConnected;
                    bool isActuallyConnected = connectedByName.ContainsKey(deviceName);

                    if (isActuallyConnected && !isCurrentlyConnected)
                    {
                        if (connectedByName.TryGetValue(deviceName, out var connectedDeviceInfo))
                            _ = ProcessDeviceAsync(connectedDeviceInfo);
                    }
                    else if (!isActuallyConnected && isCurrentlyConnected)
                    {
                        HandleDeviceDisconnected(deviceName);
                    }
                    else if (isActuallyConnected && kvp.Value.ConnectionType == DeviceConnectionType.BluetoothClassic)
                    {
                        await TryReadHfpBatteryViaCfgMgrAsync(deviceName).ConfigureAwait(false);
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _stateVerificationRunning, 0);
            }
        }

        private async Task RetryDisconnectedDevicesAsync()
        {
            await Task.CompletedTask.ConfigureAwait(false);
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

                StopDeviceWatchers();

                if (_startupConfigurationTimer != null)
                {
                    _startupConfigurationTimer.Stop();
                    _startupConfigurationTimer.Tick -= OnStartupConfigurationTimerTick;
                    _startupConfigurationTimer.Dispose();
                    _startupConfigurationTimer = null;
                }

                foreach (var device in _devices.Values)
                {
                    if (device.BatteryCharacteristic != null)
                    {
                        try
                        {
                            device.BatteryCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None
                            ).AsTask().Wait(1000);
                        }
                        catch { }
                    }

                    device.BluetoothDevice?.Dispose();
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

                if (_sentinelIcon != null)
                {
                    _sentinelIcon.Visible = false;
                    _sentinelIcon.ContextMenuStrip?.Dispose();
                    _sentinelIcon.Dispose();
                    _sentinelIcon = null;
                }

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
        private class DeviceInfo
        {
            public string Name { get; set; } = string.Empty;
            public int? BatteryLevel { get; set; }
            public DateTime? LastUpdate { get; set; }
            public bool IsConnected { get; set; }
            public BluetoothLEDevice? BluetoothDevice { get; set; }
            public GattCharacteristic? BatteryCharacteristic { get; set; }
            public string? DeviceId { get; set; }
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
