// The real updater is linked into the portable tests. Native UI entry points
// deliberately fail if a test reaches them; no application or installer runs.
namespace System.Windows.Forms;

internal sealed class Timer : IDisposable
{
    public int Interval { get; set; }
    public event EventHandler? Tick
    {
        add => throw new PlatformNotSupportedException("Native timer is not used by updater lifecycle tests.");
        remove { }
    }
    public void Start() => throw new PlatformNotSupportedException();
    public void Dispose() { }
}

internal static class Application
{
    public static string ExecutablePath => throw new PlatformNotSupportedException();
    public static void Exit() => throw new PlatformNotSupportedException();
}

internal enum MessageBoxButtons { OK }
internal enum MessageBoxIcon { Error }
internal static class MessageBox
{
    public static void Show(string message, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => throw new PlatformNotSupportedException("Native dialogs are not used by updater lifecycle tests.");
}
