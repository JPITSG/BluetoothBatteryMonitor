using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BluetoothBatteryMonitor
{
    internal static class Program
    {
        private static Mutex? _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            if (HasListDevicesFlag(args))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ShowDeviceListAndExitAsync().GetAwaiter().GetResult();
                return;
            }

            bool configure = HasConfigureFlag(args);

            // Single instance check
            const string mutexName = "Global\\BluetoothBatteryMonitor_SingleInstance";
            _mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running. If we were launched with
                // /configure, ask that instance to open its configuration dialog.
                if (configure)
                {
                    SignalShowConfiguration();
                }
                return;
            }

            try
            {
                // CRITICAL: SetHighDpiMode must be called FIRST, before any other Application calls
                // This is the .NET 6+ way to configure DPI awareness
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using var monitor = new BatteryMonitor(configure);
                Application.Run(monitor);
            }
            finally
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
        }

        private static bool HasListDevicesFlag(string[] args)
        {
            return args.Any(arg =>
            {
                var trimmed = arg.TrimStart('-', '/');
                return string.Equals(trimmed, "listdevices", StringComparison.OrdinalIgnoreCase);
            });
        }

        private static bool HasConfigureFlag(string[] args)
        {
            return args.Any(arg =>
            {
                var trimmed = arg.TrimStart('-', '/');
                return string.Equals(trimmed, "configure", StringComparison.OrdinalIgnoreCase);
            });
        }

        private static void SignalShowConfiguration()
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(BatteryMonitor.ShowConfigurationEventName);
                evt.Set();
            }
            catch
            {
                // The running instance may predate this feature or the event may
                // be unavailable; nothing more we can do from here.
            }
        }

        private static async Task ShowDeviceListAndExitAsync()
        {
            try
            {
                var leSelector = BluetoothLEDevice.GetDeviceSelector();
                var classicSelector = BluetoothDevice.GetDeviceSelector();
                var leDevices = await DeviceInformation.FindAllAsync(leSelector);
                var classicDevices = await DeviceInformation.FindAllAsync(classicSelector);
                var names = leDevices
                    .Concat(classicDevices)
                    .Select(device => device.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var message = BuildDeviceListMessage(names);
                MessageBox.Show(message, "Bluetooth Battery Monitor - Devices", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to enumerate Bluetooth devices.\n{ex.Message}",
                    "Bluetooth Battery Monitor - Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static string BuildDeviceListMessage(System.Collections.Generic.IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return "No Bluetooth devices found.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Bluetooth devices:");
            foreach (var name in names)
            {
                builder.AppendLine(name);
            }

            return builder.ToString().TrimEnd();
        }
    }
}
