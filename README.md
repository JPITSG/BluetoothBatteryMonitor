# Bluetooth Battery Monitor

A .NET 8 Windows system tray application that monitors battery levels for Bluetooth devices.

## Features

- Per-device system tray icons with battery level indicators (full, good, medium, low, empty, unknown)
- One monitored device always keeps its tray icon. With multiple devices, only connected devices appear; when all disconnect, the last visible icon remains with a **Disconnected** status. With none configured, a configuration icon stays available.
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
make test     # connection-state and tray-visibility regression checks
```

Output: `release/BluetoothBatteryMonitor.exe`

## License

[MIT](LICENSE)

## Updates

Version: **1.0.4**.

Configuration includes **Update** and **Automatically check for updates**
(enabled by default). Automatic checks run at startup, when configuration opens,
and every 60 minutes. A newer build opens configuration; **Ignore this version**
persists across restarts and only affects automatic checks.

The updater downloads the executable from this repository's
`main/release/BluetoothBatteryMonitor.exe` on GitHub, validates its size, architecture,
product identity and embedded Windows version, and shows both versions. Downloads
show progress and speed and can be cancelled. **Update** replaces the
executable and restarts the app; **Force update** reinstalls an equal version.
Older versions are never installed. A helper waits for the running app to exit,
keeps a backup during replacement, and restores it if launching the new executable
fails. Protected installation folders request Windows elevation. After a successful
update, configuration displays the installed version.
