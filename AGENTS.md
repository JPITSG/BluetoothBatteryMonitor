# Agent instructions

## Version policy

- Every commit must increment the application patch version by `0.0.1` (for example, `1.0.0` becomes `1.0.1`). The initial version introduced in this commit is `1.0.0`.
- Keep `BluetoothBatteryMonitor.csproj`, `assets/package.json`, and `assets/package-lock.json` versions synchronized.
- Run `make` before committing and include `release/BluetoothBatteryMonitor.exe` so its embedded version matches the committed sources.

## Project notes

- .NET 8 Windows Forms tray application with a React/WebView2 configuration dialog.
- Build on Linux with `make` (.NET 8 SDK, Node.js, GNU Make required).
- GitHub updates download `release/BluetoothBatteryMonitor.exe` from this repository's `main` branch and compare embedded Windows file versions. Preserve cancellation, download validation, downgrade prevention, and rollback behavior.
- The configuration footer shows only the application version beside Cancel; do not add the WebView2 runtime version.
