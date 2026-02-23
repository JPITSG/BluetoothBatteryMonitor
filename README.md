# Bluetooth Battery Monitor

A .NET 8 Windows system tray application that monitors battery levels for Bluetooth devices.

## Features

- Per-device system tray icons with battery level indicators (full, good, medium, low, empty)
- Supports Bluetooth LE (GATT Battery Service), Bluetooth Classic (HFP via CfgMgr32), and Windows device property fallback
- WebView2 configuration dialog for selecting which paired devices to monitor
- Device configuration persisted in Windows Registry (`HKCU\SOFTWARE\JPIT\BluetoothBatteryMonitor`)
- Automatic device connect/disconnect detection via DeviceWatcher with periodic state verification
- DPI-aware with icon refresh on display/session changes (including RDP reconnect)
- Single-instance enforcement

## Getting Started

On first launch, a sentinel battery icon appears in the tray. Right-click it and select **Configuration** to choose which paired Bluetooth devices to monitor. Selected devices will each get their own tray icon showing battery status.

## Usage

- **Right-click** any tray icon for status info, configuration, or to exit
- **Double-click** any device icon to open Windows Bluetooth settings
- **`--listdevices`** flag: shows all paired Bluetooth devices in a dialog and exits

## Prerequisites

### Building (Debian/Linux):
- .NET 8 SDK
- Node.js
- GNU Make

### Running (Windows 10/11):
- .NET 8 Desktop Runtime — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime — included with Windows 11; [download for Windows 10](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)
- Windows 10 build 19041 or later

## Building

```bash
make          # full build: frontend + .NET publish
make clean    # remove all build artifacts
```

Output: `release/BluetoothBatteryMonitor.exe`

## License

[MIT](LICENSE)
