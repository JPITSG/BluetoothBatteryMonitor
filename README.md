# Bluetooth Battery Monitor

A .NET 8 Windows system tray application that monitors battery levels for Bluetooth devices.

## Features

- Per-device system tray icons with battery level indicators (full, good, medium, low, empty)
- One monitored device always keeps its tray icon. With multiple devices, only connected devices appear; when all disconnect, the last visible icon remains with a **Disconnected** status. With none configured, a configuration icon stays available.
- Supports Bluetooth LE (GATT Battery Service), Bluetooth Classic (HFP via CfgMgr32), and Windows device property fallback
- WebView2 configuration dialog for selecting which paired devices to monitor, with checked devices first and live connected/disconnected and battery status beside each monitored device checkbox
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
- Right-click opens only the tray menu. Choose **Configuration** on any device's
  menu to open configuration or restore the existing window to the foreground,
  preserving unsaved edits. Configuration remains available in the taskbar.
- **Double-click** any device icon to open Windows Bluetooth settings
- **`--listdevices`** flag: shows all paired Bluetooth devices in a dialog and exits

## Battery history

Battery readings for enabled, monitored devices are saved locally in
`%LOCALAPPDATA%\BluetoothBatteryMonitor\battery-history\` (usually
`C:\Users\<you>\AppData\Local\BluetoothBatteryMonitor\battery-history\`).
Each device has a JSON file, named with a stable hash of its case-insensitive
configured name; its readable device name is included inside the file.

The first known percentage is saved, then only percentage changes trigger a
save. Each file retains the newest **1,000 entries**, containing the observed
percentage and a UTC observation timestamp. Windows' cached readings are logged
when observed; Windows does not provide their original measurement time.
Disconnected/unknown values do not create synthetic readings. Actual reported
zeroes are logged, but follow the app's disconnected display policy.

A rise of **at least five percentage points compared with the previous nonzero
reading** marks a charge. Smaller individual increases do not trigger it. Zeroes
are excluded from the comparison so a disconnected sentinel cannot fabricate a
charge. The first usable reading establishes the baseline. **Last charged**
appears below the device's connection status in configuration, in local time,
and remains visible while disconnected. This is the time the increase was
observed, not the exact time charging began or ended. Until a charge is detected,
the line shows **Last charged · Collecting data**. The last-charge timestamp
survives restarts and log trimming.

File operations run in the background; changed logs are replaced atomically and
pending saves finish on normal exit or update. Duplicate readings, including
after restarting, do not rewrite the log. Turning monitoring off stops new
logging after saving the selection; existing history is retained for re-enabling.
Unreadable/write-failed logs are retried on later readings and errors go to
`diagnostics.log`; invalid JSON is preserved in a `.corrupt-*` backup before
starting a fresh log. History stays on this PC and is not uploaded.

For a dummy-data configuration preview, build the frontend and open
`http://127.0.0.1:8782/tests/config-preview.html` using the local server described
below. It uses the real built modal and simulated devices, without changing
settings or writing any history.

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
make test     # connection, tray, update-download, and battery-history regression checks
```

Output: `release/BluetoothBatteryMonitor.exe`

To run the configuration UI checks, build the frontend with `make frontend`,
serve the repository with `python3 -m http.server 8782 --bind 127.0.0.1`, and open
`http://127.0.0.1:8782/tests/config-ui.html` in a browser. This uses a simulated
WebView host to check immediate update/cancel feedback, delayed device discovery,
selection preservation, live download speed, per-update reopening, and scrolling.
Native WebView2 startup and the installer handoff still require Windows verification.

## License

[MIT](LICENSE)

## Updates

Version: **1.0.19**.

Configuration includes **Update** and **Automatically check for updates**
(enabled by default). Automatic checks run at startup, when configuration opens,
and every 60 minutes. A newer build opens configuration; **Ignore this version**
persists across restarts and only affects automatic checks.

The updater downloads the executable from this repository's
`main/release/BluetoothBatteryMonitor.exe` on GitHub, validates its size, architecture,
product identity and embedded Windows version, and shows both versions. Downloads
show progress in the tooltip and live speed in the red button, for example
**Checking (100kb/s)...**. Speed is rounded to whole kilobytes per second
(1 KB = 1,024 bytes), sampled every 250 ms, and falls to zero when transfer stalls.
Click the red button to cancel the check/download. **Update** replaces the
executable and restarts the app; **Force update** reinstalls an equal version.
Older versions are never installed. A helper waits for the running app to exit,
keeps a backup during replacement, and restores it if launching the new executable
fails. Protected installation folders request Windows elevation. The confirmation
includes an unchecked **Reopen settings after update** checkbox.
Check it to reopen configuration after a successful update and restart; otherwise
the app restarts in the tray with settings closed. This choice applies only to
that confirmation and is not saved. Configuration displays the installed version
when next opened after the update.

## Connection troubleshooting

After startup, wake from sleep/hibernation, or session unlock, the app checks
Windows' connection/battery state immediately after a short debounce and retries
every three seconds for 30 seconds while Bluetooth settles. Devices recover
independently, so an idle peripheral cannot hold up another device's battery.
Existing LE battery characteristics are re-read when a percentage is missing,
a previous read/subscription failed, or a wake refresh was requested. Windows'
GATT cache is checked first, followed by a live read and notification subscription.
Failed read handles are rediscovered; individual battery reads/subscriptions time
out after ten seconds. After the recovery period, regular polling continues.
Cached values only fill an unknown percentage and never establish a connection.

Connection state follows Windows' explicit connection flag for each paired
Bluetooth endpoint. The Bluetooth object's status is a fallback when Windows
does not provide that flag. Battery values never establish a connection.
If Windows rejects a native device ID or returns no device, that endpoint's
cached connected flag is discarded and monitoring tries another paired endpoint
with the same name. This handles stale entries left by other Bluetooth adapters.
Rejected endpoints remain eligible on later polling attempts. Diagnostic errors
include the device name and endpoint ID to distinguish duplicate entries.

Device enumeration retries with connection-only properties when battery-property
queries fail. Errors are recorded locally in
`%LOCALAPPDATA%\BluetoothBatteryMonitor\diagnostics.log` (rotated at 256 KB).
