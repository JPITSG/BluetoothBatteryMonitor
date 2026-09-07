# Bluetooth Battery Monitor

A .NET 8 Windows system tray application that monitors battery levels for Bluetooth devices.

## Features

- Per-device system tray icons with battery level indicators (full, good, medium, low, empty)
- One monitored device always keeps its tray icon. With multiple devices, only connected devices appear; when all disconnect, the last visible icon remains with a **Disconnected** status. With none configured, a configuration icon stays available.
- Supports Bluetooth LE (GATT Battery Service), Bluetooth Classic (HFP via CfgMgr32), and Windows device property fallback
- WebView2 configuration dialog for selecting which paired devices to monitor
- Device configuration persisted in Windows Registry (`HKCU\SOFTWARE\JPIT\BluetoothBatteryMonitor`)
- Zero-percent readings follow disconnected status and tray visibility rules. Disconnected and unknown batteries use the plain empty-battery icon.
- Each device has a stable tray GUID so Explorer can retain its preferences across app restarts, updates, and connection changes. After upgrading from older icons, arrange the icons once; subsequent launches reuse those identities. Keep the executable at the same path.
- Startup uses Windows battery properties and cached Bluetooth battery data for devices confirmed connected, then refreshes from the device
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
make test     # connection, tray, and update-download regression checks
```

Output: `release/BluetoothBatteryMonitor.exe`

To run the configuration UI checks, build the frontend with `make frontend`,
serve the repository with `python3 -m http.server 8782 --bind 127.0.0.1`, and open
`http://127.0.0.1:8782/tests/config-ui.html` in a browser. This uses a simulated
WebView host to check immediate update/cancel feedback, delayed device discovery,
selection preservation, and scrolling. Native WebView2 startup and the installer
handoff still require Windows verification.

## License

[MIT](LICENSE)

## Updates

Version: **1.0.9**.

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

## Connection troubleshooting

Connection state follows Windows' explicit connection flag for each paired
Bluetooth endpoint. The Bluetooth object's status is a fallback when Windows
does not provide that flag. Battery values never establish a connection.

Device enumeration retries with connection-only properties when battery-property
queries fail. Errors are recorded locally in
`%LOCALAPPDATA%\BluetoothBatteryMonitor\diagnostics.log` (rotated at 256 KB).
