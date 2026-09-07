using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BluetoothBatteryMonitor;

// WinForms NotifyIcon does not expose NIF_GUID. Use Shell_NotifyIcon directly
// while keeping WinForms menus and the monitor's existing event handlers.
internal sealed class PersistentTrayIcon : NativeWindow, IDisposable
{
    private readonly TrayIconRegistration _registration;
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly Timer _retry = new() { Interval = 2000 };
    private Icon? _icon;
    private string _text = "";
    private bool _visible;
    private bool _disposed;
    private ContextMenuStrip? _menu;
    public event EventHandler? DoubleClick;

    public PersistentTrayIcon(Guid identity)
    {
        _registration = new TrayIconRegistration(identity, Send);
        // A hidden top-level window receives Explorer's TaskbarCreated broadcast;
        // message-only windows do not. No WS_VISIBLE or taskbar button is used.
        CreateHandle(new CreateParams { Caption = "Bluetooth battery notification", Style = unchecked((int)0x80000000) });
        _retry.Tick += (_, _) => Synchronize();
    }

    public Icon? Icon
    {
        get => _icon;
        set { if (!ReferenceEquals(_icon, value)) { _icon = value; Synchronize(); } }
    }
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; Synchronize(); } }
    }
    public bool Visible
    {
        get => _visible;
        set { if (_visible != value) { _visible = value; Synchronize(); } }
    }
    public ContextMenuStrip? ContextMenuStrip
    {
        get => _menu;
        set
        {
            if (_menu != null) _menu.Closed -= OnMenuClosed;
            _menu = value;
            if (_menu != null) _menu.Closed += OnMenuClosed;
        }
    }

    private void Synchronize()
    {
        if (_disposed) return;
        _retry.Enabled = !_registration.Update(Handle, _icon?.Handle ?? IntPtr.Zero, _text, _visible);
    }

    private static bool Send(TrayCommand command, TrayIconData data) => Shell_NotifyIconW(command, ref data);

    protected override void WndProc(ref Message message)
    {
        if (_taskbarCreated != 0 && message.Msg == _taskbarCreated)
        {
            _registration.ExplorerRestarted();
            Synchronize();
        }
        else if (message.Msg == TrayIconRegistration.CallbackMessage && !_disposed)
        {
            int notification = (int)(message.LParam.ToInt64() & 0xffff);
            switch (notification)
            {
                case 0x203: // WM_LBUTTONDBLCLK
                    DoubleClick?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x7b: // WM_CONTEXTMENU, including keyboard context-menu key
                case 0x401: // NIN_KEYSELECT: Enter/Space opens the menu
                    ShowMenu(message.WParam);
                    break;
                case 0x205 when !_registration.Version4: // Legacy WM_RBUTTONUP
                    ShowMenu(IntPtr.Zero);
                    break;
            }
        }
        base.WndProc(ref message);
    }

    private void ShowMenu(IntPtr coordinates)
    {
        if (_menu == null || _menu.IsDisposed || _menu.Visible) return;
        var point = Cursor.Position;
        if (_registration.Version4)
        {
            long packed = coordinates.ToInt64();
            var anchor = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            if (anchor != new Point(-1, -1)) point = anchor;
        }
        SetForegroundWindow(Handle);
        _menu.Show(point);
        PostMessageW(Handle, 0, IntPtr.Zero, IntPtr.Zero); // Allow outside-click dismissal.
    }

    private void OnMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        if (!_disposed) _registration.ReturnFocus(Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Dispose();
        if (_menu != null) _menu.Closed -= OnMenuClosed;
        _registration.Remove(Handle);
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(TrayCommand command, ref TrayIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
