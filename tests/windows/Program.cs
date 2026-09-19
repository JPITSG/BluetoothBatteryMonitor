using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using BluetoothBatteryMonitor;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var assembly = Assembly.GetExecutingAssembly();
foreach (string level in new[] { "empty", "low", "medium", "good", "full" })
{
    string resource = $"icon_battery_{level}.ico";
    using var baseline = TrayIconRenderer.Load(assembly, resource, new Size(16, 16));
    using var baselinePixels = baseline.ToBitmap();
    using var stream = assembly.GetManifestResourceStream($"BluetoothBatteryMonitor.{resource}")!;
    using var original = new Icon(stream, 16, 16);
    using var originalPixels = original.ToBitmap();
    for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            Check(baselinePixels.GetPixel(x, y) == originalPixels.GetPixel(x, y), "100% scaling must use the original 16px artwork without resampling.");

    // Includes fractional scaling and repeated RDP -> local transitions.
    foreach (int pixels in new[] { 32, 16, 20, 24, 28, 32, 36, 40, 48, 64, 16 })
    {
        using var icon = TrayIconRenderer.Load(assembly, resource, new Size(pixels, pixels));
        Check(icon.Size == new Size(pixels, pixels), $"{resource} must return a {pixels}px HICON.");
        using var bitmap = icon.ToBitmap();
        Check(bitmap.GetPixel(0, 0).A == 0, "Resized icons must retain transparent corners.");
        if (pixels == 16)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    Check(bitmap.GetPixel(x, y) == baselinePixels.GetPixel(x, y), "Returning to local DPI must restore the original pixels.");
    }
}

using (var fallback = TrayIconRenderer.CreateFallback(new Size(20, 20)))
    Check(fallback.Size == new Size(20, 20), "Fallback artwork must also match the taskbar size.");

using var process = Process.GetCurrentProcess();
uint before = GetGuiResources(process.Handle, 1); // GR_USEROBJECTS includes HICONs.
for (int iteration = 0; iteration < 200; iteration++)
{
    using var icon = TrayIconRenderer.Load(assembly, "icon_battery_full.ico", new Size(20, 20));
    using var fallback = TrayIconRenderer.CreateFallback(new Size(20, 20));
}
Check(GetGuiResources(process.Handle, 1) <= before + 2, "Repeated refreshes must release their native icon handles without waiting for GC.");
Console.WriteLine($"Passed {checks} Windows tray rendering checks.");

[DllImport("user32.dll", ExactSpelling = true)]
static extern uint GetGuiResources(IntPtr process, uint flags);
