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
    private readonly Guid _identity;
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly Timer _retry = new() { Interval = 2000 };
    private Icon? _icon;
    private string _text = "";
    private bool _visible;
    private bool _disposed;
    private bool _repaintPending;
    private ContextMenuStrip? _menu;
    public event EventHandler? DoubleClick;
    public event EventHandler? ExplorerRestarted;

    public PersistentTrayIcon(Guid identity)
    {
        _identity = identity;
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
        if (!_retry.Enabled && _visible && _repaintPending)
        {
            _repaintPending = !Repaint();
            _retry.Enabled = _repaintPending;
        }
    }

    private static bool Send(TrayCommand command, TrayIconData data) => Shell_NotifyIconW(command, ref data);

    public void RefreshRegistration()
    {
        if (_disposed) return;
        _registration.RequestRecreation();
        _repaintPending = true;
        Synchronize();
    }

    private bool Repaint()
    {
        var identity = new IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = Handle, Id = 1, Identity = _identity
        };
        if (Shell_NotifyIconGetRect(ref identity, out var rectangle) != 0) return false;
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        MapWindowPoints(IntPtr.Zero, taskbar, ref rectangle, 2);
        // Explorer can keep the old pixels on screen until the mouse hovers
        // over the icon, even after it accepts the new image/registration.
        // Invalidate just this icon's area, including the tray child window.
        return RedrawWindow(taskbar, ref rectangle, IntPtr.Zero, 0x0181); // RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW
    }

    protected override void WndProc(ref Message message)
    {
        if (_taskbarCreated != 0 && message.Msg == _taskbarCreated)
        {
            _registration.ExplorerRestarted();
            Synchronize();
            ExplorerRestarted?.Invoke(this, EventArgs.Empty);
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
        // A selected command may activate configuration or Windows Settings;
        // only return keyboard focus to the tray when dismissing the menu.
        if (!_disposed && e.CloseReason != ToolStripDropDownCloseReason.ItemClicked)
            _registration.ReturnFocus(Handle);
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
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Identity;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identity, out NativeRectangle rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string className, string? windowName);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref NativeRectangle rectangle, uint count);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, ref NativeRectangle rectangle, IntPtr region, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
