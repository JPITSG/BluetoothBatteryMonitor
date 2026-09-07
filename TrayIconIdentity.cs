using System;
using System.Security.Cryptography;
using System.Text;

namespace BluetoothBatteryMonitor;

internal static class TrayIconIdentity
{
    // Stable across process restarts, updates, configuration ordering and casing.
    // Keep this namespace unchanged: changing it resets Explorer's preferences.
    private const string IdentityNamespace = "JPIT.BluetoothBatteryMonitor.Tray.v1/";
    public static Guid ForDevice(string name) => Create("device/" + name.Normalize(NormalizationForm.FormC).ToUpperInvariant());
    public static Guid Configuration => Create("configuration");

    private static Guid Create(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(IdentityNamespace + key)).AsSpan(0, 16));
}
