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
        // Defer icons that have never been shown until they are.
        if (!_registered && (!visible || icon == IntPtr.Zero)) return true;

        var data = Identify(window);
        data.Flags |= TrayFlags.Message | TrayFlags.Icon | TrayFlags.Tip | TrayFlags.State | TrayFlags.ShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = icon;
        data.Tip = text.Length > 127 ? text[..127] : text;
        data.StateMask = 1; // NIS_HIDDEN
        data.State = visible ? 0u : 1u;

        // Explorer rebuilds its notification area during RDP and console
        // transitions. That can leave the shell holding this GUID after the
        // app was told to register again, or drop it without telling the app
        // at all. NIM_ADD always fails against a registration the shell still
        // holds, and NIM_MODIFY always fails once it has dropped one, so a
        // single wrong guess would stop this icon from ever publishing again
        // and the shell would keep drawing the image it last accepted - the
        // previous session's size, rescaled and blurred. Try the opposite
        // command before reporting a failure the retry timer cannot fix.
        if (_registered)
        {
            if (Publish(TrayCommand.Modify, data, window)) return true;
            _registered = false;
            Version4 = false;
            return Publish(TrayCommand.Add, data, window);
        }

        return Publish(TrayCommand.Add, data, window) || Publish(TrayCommand.Modify, data, window);
    }

    // A shell command that succeeds proves the icon is registered. Claim the
    // registration only then, and (re-)apply the callback version whenever a
    // new one is adopted so mouse and keyboard messages keep working.
    private bool Publish(TrayCommand command, TrayIconData data, IntPtr window)
    {
        if (!_send(command, data)) return false;
        bool adopted = !_registered;
        _registered = true;
        if (!adopted && Version4) return true;
        var version = Identify(window);
        version.Version = 4; // NOTIFYICON_VERSION_4
        Version4 = _send(TrayCommand.SetVersion, version);
        return true;
    }

    public void ExplorerRestarted() { _registered = false; Version4 = false; }
    public void ReturnFocus(IntPtr window) { if (_registered) _send(TrayCommand.SetFocus, Identify(window)); }
    public void Remove(IntPtr window)
    {
        if (_registered) _send(TrayCommand.Delete, Identify(window));
        _registered = false;
        Version4 = false;
    }
}
