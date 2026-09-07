using System;
using System.Collections.Generic;
using System.Linq;

namespace BluetoothBatteryMonitor;

// Mutated on the monitor's UI context. A generation identifies one connection
// attempt/session, so an old asynchronous read cannot update a reconnected device.
internal class MonitorDeviceState
{
    internal MonitorDeviceState(string name) => Name = name;
    public string Name { get; }
    public string? DeviceId { get; private set; }
    public long Generation { get; private set; }
    public bool IsConnected { get; private set; }
    public int? BatteryLevel { get; private set; }
    public DateTime? LastUpdate { get; private set; }
    // Keep the transport subscription alive so a later positive reading can
    // restore the icon, but treat zero exactly like disconnected in the UI.
    public bool IsConnectedForDisplay => IsConnected && BatteryLevel != 0;
    public DeviceStatus DisplayStatus => new(Name, IsConnectedForDisplay, IsConnectedForDisplay ? BatteryLevel : null);
    public string StatusText => !IsConnectedForDisplay ? "Disconnected" :
        BatteryLevel.HasValue ? $"Battery: {BatteryLevel}%" : "Connected (battery unknown)";

    public long BeginConnection(string deviceId)
    {
        Disconnect();
        DeviceId = deviceId;
        return Generation;
    }

    public bool ConfirmConnection(long generation, bool connectionConfirmed)
    {
        if (generation != Generation || !connectionConfirmed) return false;
        IsConnected = true;
        return true;
    }

    public void Disconnect()
    {
        Generation++;
        IsConnected = false;
        BatteryLevel = null;
        LastUpdate = null;
    }

    public bool TrySeedBattery(long generation, int level)
    {
        if (!IsConnected || generation != Generation || BatteryLevel.HasValue || level is < 0 or > 100) return false;
        BatteryLevel = level;
        // Windows does not supply the age of its cache. Do not label it as a
        // fresh device reading; live reads/notifications will set LastUpdate.
        LastUpdate = null;
        return true;
    }

    public bool TryUpdateBattery(long generation, int level, DateTime timestamp)
    {
        if (!IsConnected || generation != Generation || level is < 0 or > 100) return false;
        BatteryLevel = level;
        LastUpdate = timestamp;
        return true;
    }
}

internal readonly record struct DeviceStatus(string Name, bool Online, int? BatteryLevel);

internal readonly record struct TrayDevice(string Name, bool Connected, bool Visible);

internal static class TrayVisibility
{
    public static HashSet<string> SelectVisible(IReadOnlyList<TrayDevice> devices)
    {
        var visible = devices.Where(d => d.Connected).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (visible.Count == 0 && devices.Count > 0)
        {
            // Retain the last visible device when it disconnects. On startup,
            // use the first configured device until a connection is found.
            visible.Add(devices.FirstOrDefault(d => d.Visible, devices[0]).Name);
        }
        return visible;
    }
}

// Windows' paired endpoint state describes the system connection. A WinRT
// Bluetooth object can lag behind it; it is only a fallback when unavailable.
internal static class ConnectionEvidence
{
    public static bool IsConnected(bool paired, bool? windowsConnected, bool nativeConnected) =>
        windowsConnected switch
        {
            false => false,
            true when paired => true,
            _ => nativeConnected
        };
}
