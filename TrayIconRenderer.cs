using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BluetoothBatteryMonitor;

internal static class TrayIconRenderer
{
    public static Size? GetTaskbarIconSize()
    {
        // The taskbar can be on a different monitor from our hidden windows.
        // GetDeviceCaps(LOGPIXELS*) reports system DPI, not its current DPI.
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        uint dpi = taskbar == IntPtr.Zero ? 0 : GetDpiForWindow(taskbar);
        if (dpi == 0) return null;

        int width = GetSystemMetricsForDpi(49, dpi);  // SM_CXSMICON
        int height = GetSystemMetricsForDpi(50, dpi); // SM_CYSMICON
        return width > 0 && height > 0 ? new Size(width, height) : null;
    }

    public static Icon Load(Assembly assembly, string resourceName, Size size)
    {
        using var stream = assembly.GetManifestResourceStream($"BluetoothBatteryMonitor.{resourceName}")
            ?? throw new InvalidOperationException($"Missing tray icon resource: {resourceName}");

        // These resources contain 16, 32 and 48px artwork. Use an exact frame
        // when available, otherwise scale down a larger original (e.g. 32 to
        // 20px at 125%). Never rescale an icon from the previous RDP session.
        int requested = Math.Max(size.Width, size.Height);
        int sourceSize = requested <= 16 ? 16 : requested <= 32 ? 32 : 48;
        using var source = new Icon(stream, sourceSize, sourceSize);
        if (source.Size == size) return (Icon)source.Clone();

        using var original = source.ToBitmap();
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(original, new Rectangle(Point.Empty, size),
                0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
        }
        return FromBitmap(bitmap);
    }

    public static Icon CreateFallback(Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Gray);
            using var pen = new Pen(Color.Red, Math.Max(1, size.Width / 8f));
            graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
        }
        return FromBitmap(bitmap);
    }

    private static Icon FromBitmap(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            // FromHandle does not own the HICON. Clone it so Icon.Dispose
            // releases the copy, and always destroy the temporary original.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string className, string? windowName);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
