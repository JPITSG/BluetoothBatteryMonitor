using System;
using System.Runtime.InteropServices;

namespace BluetoothBatteryMonitor;

internal enum TrayCommand : uint { Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4 }

[Flags]
internal enum TrayFlags : uint { Message = 1, Icon = 2, Tip = 4, State = 8, Guid = 0x20, ShowTip = 0x80 }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TrayIconData
{
    public uint Size;
    public IntPtr Window;
    public uint Id;
    public TrayFlags Flags;
    public uint CallbackMessage;
    public IntPtr Icon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
    public uint State;
    public uint StateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
    public uint Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
    public uint InfoFlags;
    public Guid Identity;
    public IntPtr BalloonIcon;
}

// The shell-facing lifecycle is separate from the message window so that
// reconnect/restart behavior can be regression-tested without Windows Explorer.
internal sealed class TrayIconRegistration
{
    public const int CallbackMessage = 0x8001;
    private readonly Guid _identity;
    private readonly Func<TrayCommand, TrayIconData, bool> _send;
    private bool _registered;
    private bool _recreatePending;
    public bool Version4 { get; private set; }

    public TrayIconRegistration(Guid identity, Func<TrayCommand, TrayIconData, bool> send)
    {
        _identity = identity;
        _send = send;
    }

    private TrayIconData Identify(IntPtr window) => new()
    {
        Size = (uint)Marshal.SizeOf<TrayIconData>(), Window = window, Id = 1,
        Flags = TrayFlags.Guid, Identity = _identity, Tip = "", Info = "", InfoTitle = ""
    };

    public bool Update(IntPtr window, IntPtr icon, string text, bool visible)
    {
        // Explorer can retain a resampled image after RDP -> console even
        // when NIM_MODIFY receives a fresh, correctly sized HICON. A new
        // registration clears that cached image; keep the published GUID.
        // Defer hidden icons until they are actually shown again.
        if (_recreatePending && _registered && visible && icon != IntPtr.Zero)
        {
            if (!_send(TrayCommand.Delete, Identify(window))) return false;
            _registered = false;
            Version4 = false;
        }
        if (!_registered && (!visible || icon == IntPtr.Zero)) return true;
        var data = Identify(window);
        data.Flags |= TrayFlags.Message | TrayFlags.Icon | TrayFlags.Tip | TrayFlags.State | TrayFlags.ShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = icon;
        data.Tip = text.Length > 127 ? text[..127] : text;
        data.StateMask = 1; // NIS_HIDDEN
        data.State = visible ? 0u : 1u;
        if (_registered) return _send(TrayCommand.Modify, data);
        if (!_send(TrayCommand.Add, data)) return false;
        _registered = true;
        _recreatePending = false;
        var version = Identify(window);
        version.Version = 4; // NOTIFYICON_VERSION_4
        Version4 = _send(TrayCommand.SetVersion, version);
        return true;
    }

    public void RequestRecreation() => _recreatePending = true;
    public void ExplorerRestarted() { _registered = false; _recreatePending = false; Version4 = false; }
    public void ReturnFocus(IntPtr window) { if (_registered) _send(TrayCommand.SetFocus, Identify(window)); }
    public void Remove(IntPtr window)
    {
        if (_registered) _send(TrayCommand.Delete, Identify(window));
        _registered = false;
        _recreatePending = false;
        Version4 = false;
    }
}
