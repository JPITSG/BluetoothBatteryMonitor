using System.Windows.Forms;

namespace BluetoothBatteryMonitor;

internal sealed class TrayContextMenuStrip : ContextMenuStrip
{
    public TrayContextMenuStrip()
    {
        // .NET 8's parameterless menu constructor leaves the image margin and
        // text padding at 96 DPI. Opening on the initial monitor does not raise
        // a DPI change, so those constants can stay smaller than the menu text.
        // Initialize them before adding items (and any explicit item fonts).
        // WinForms continues to handle subsequent changes between monitors.
        base.RescaleConstantsForDpi(96, DeviceDpi);
    }
}
