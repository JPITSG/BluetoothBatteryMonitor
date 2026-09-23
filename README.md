# Bluetooth Battery Monitor

A .NET 8 Windows system tray application that monitors battery levels for Bluetooth devices.

## Features

- Per-device system tray icons with battery level indicators (full, good, medium, low, empty)
- One monitored device always keeps its tray icon. With multiple devices, only connected devices appear; when all disconnect, the last visible icon remains with a **Disconnected** status. With none configured, a configuration icon stays available.
- Supports Bluetooth LE (GATT Battery Service), Bluetooth Classic (HFP via CfgMgr32), and Windows device property fallback
- WebView2 configuration dialog for selecting which paired devices to monitor, with checked devices first (connected ones leading), live connected/disconnected and battery status beside each monitored device checkbox, and a small battery trend graph for the current discharge or charge. Only the checkbox toggles monitoring; the name, status and graph are not click targets.
- Device configuration persisted in Windows Registry (`HKCU\SOFTWARE\JPIT\BluetoothBatteryMonitor`)
- Optional start with Windows at sign-in (per user, no administrator rights needed)
- Zero-percent readings follow disconnected status and tray visibility rules. Disconnected and unknown batteries use the plain empty-battery icon.
- Each device has a stable tray GUID so Explorer can retain its preferences across app restarts, updates, and connection changes. After upgrading from older icons, arrange the icons once; subsequent launches reuse those identities. Keep the executable at the same path.
- Startup uses Windows battery properties and cached Bluetooth battery data for devices confirmed connected, then refreshes from the device
- Automatic device connect/disconnect detection via DeviceWatcher with periodic state verification
- Tray icons use the taskbar's current DPI and original artwork at the matching size. After RDP connect/disconnect, local unlock, display changes, or an Explorer restart, refreshes continue for 30 seconds while Windows restores the taskbar, and any later size change is picked up automatically. Each icon keeps its device GUID and its place in the tray: when Explorer rebuilds the notification area it can keep or drop a registration without saying which, so recovery tries both shell commands and adopts whichever the shell accepts. Without that, the shell would keep drawing the artwork published for the previous session's DPI, rescaled and blurred. Recovered icons are repainted immediately rather than waiting for a mouse hover. Hidden icons recover when next shown.
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

**Start with Windows**, above **Automatically check for updates** in
configuration, adds or removes a `BluetoothBatteryMonitor` value under
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` that launches
this executable when you sign in. It is off by default and, like automatic update
checks, applies as soon as it is toggled. An entry disabled in Task Manager's
startup apps, or one that launches another copy of the executable, shows as off;
turning the toggle on replaces or re-enables it. If you previously placed a
shortcut in your Startup folder, remove it after turning this on.

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

Connection changes are saved in the same file: when a monitored device connects
or disconnects, as configuration shows it, and when monitoring stops because the
app closes, the PC sleeps, shuts down or signs out, or the device stops being
monitored. A change is saved once it has lasted **ten seconds** and is dated from
when it began, so brief drop-outs are ignored, including the app's own
reconnections after startup, wake, unlock or a configuration change. After a crash
or power loss, a heartbeat saved every minute marks when monitoring stopped. Each
file keeps the newest **4,000 changes**. Logs from earlier versions load unchanged;
connections before this was recorded are unknown.

Beside each monitored device, configuration draws a small graph of the current
discharge or charge: the readings since the last charge ended (the peak) or since
charging last began (the trough), whichever is nearer, on a fixed 0–100% scale
with three dates or times along the bottom. A move of at least five points
against the current direction is a turning point; smaller reversals are treated
as reporting noise, matching charge detection. While a device is connected with a
known percentage, the line continues at that level to the present, because the
next change would have been logged. Otherwise the line ends at the last reading,
marked with a ring instead of a dot. Hovering the graph shows the change, when it
began, and the approximate rate per hour or per day. Devices with fewer than two
usable readings show no graph. Zero readings are never plotted.

Time a device was offline is shaded **light red** behind the line: disconnected,
or not monitored while the PC was asleep or off or the app was closed. App
restarts shorter than two minutes are not shown. Each pixel column is shaded by
how much of its time was offline, so long periods are solid bands while frequent
short drop-outs blend into an even, lighter tint instead of a comb of slivers. A
current disconnection extends the graph to the present. Hovering an offline period
a few pixels wide or more shows when it happened and for how long, and the graph's
tooltip adds how many times and how long the device was offline in total, and
since when it has been disconnected.

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
settings or writing any history. Add `?zoom=3` to the address to inspect the
graphs up close.

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
make test     # connection, tray, updater, battery-history, connection-history and trend checks
```

Output: `release/BluetoothBatteryMonitor.exe`

To run the configuration UI checks, build the frontend with `make frontend`,
serve the repository with `python3 -m http.server 8782 --bind 127.0.0.1`, and open
`http://127.0.0.1:8782/tests/config-ui.html` in a browser. This uses a simulated
WebView host to check immediate update/cancel feedback, delayed device discovery,
selection preservation, live download percentage, per-update reopening, checkbox alignment,
battery trend graphs with offline shading, and scrolling.
Native WebView2 startup and the installer handoff still require Windows verification.

On Windows, run `dotnet run --project tests/windows/TrayIconRendering.Tests.csproj -c Release`
to check native icon dimensions, transparency, original pixels after DPI round trips,
and icon-handle cleanup. The cross-platform `make test` suite checks delayed RDP/local
transitions, refresh retries, and preservation of tray identity and visibility.
For end-to-end verification, connect with `mstsc` at a different display scale, then
sign in locally and check that both battery icons return to their original sharpness.
Repeat with the same scale on both sessions to cover taskbar refreshes without a DPI change.

## License

[MIT](LICENSE)

## Updates

Version: **1.0.29**.

Configuration includes **Update** and **Automatically check for updates**
(enabled by default). Automatic checks run at startup, when configuration opens,
and every 60 minutes, including while configuration is closed and the app runs
in the tray. A newer build opens configuration with an update prompt;
**Ignore this version** persists across restarts and only affects automatic checks.

Closing configuration keeps an automatic check in progress running, so a newer
build can still open the update prompt. Manual checks are cancelled on close.
Closing configuration with a completed update prompt, including using the title
bar's **X**, dismisses that result and releases its staged download; later hourly
checks can then offer the update again. An installation already accepted is left
to finish.

The updater downloads the executable from this repository's
`main/release/BluetoothBatteryMonitor.exe` on GitHub, validates its size, architecture,
product identity and embedded Windows version, and shows both versions. While
downloading, the red button shows how much of the file has arrived, for example
**Checking (42%)...**. It updates every 250 ms and rounds down, so it reads 100%
only once the whole file is received. Its tooltip adds the transfer speed in whole
kilobytes per second (1 KB = 1,024 bytes), which falls to zero when transfer stalls.
Click the red button again to stop the check and download; the partial file is
deleted. **Update** replaces the
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
