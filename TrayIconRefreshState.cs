using System.Drawing;

namespace BluetoothBatteryMonitor;

// Session/display notifications can arrive before Explorer has restored the
// local taskbar. Refresh even if the DPI is unchanged, then let it settle.
internal sealed class TrayIconRefreshState
{
    private static readonly int[] RetryDelays = { 0, 1000, 3000, 5000, 10000, 20000, 30000 };
    private Size _lastSize;
    private long _requestedAt;
    private int _nextRetry = RetryDelays.Length;

    public void Request(long now)
    {
        _requestedAt = now;
        _nextRetry = 0;
    }

    public bool ShouldRefresh(Size? size, long now)
    {
        // Explorer may be temporarily absent during a session transition.
        // Keep both the current icons and pending retries in that case.
        if (size is not { Width: > 0, Height: > 0 } current) return false;

        bool retryDue = _nextRetry < RetryDelays.Length && now - _requestedAt >= RetryDelays[_nextRetry];
        if (current == _lastSize && !retryDue) return false;

        _lastSize = current;
        // A delayed UI tick needs one refresh, not a burst of missed attempts.
        while (_nextRetry < RetryDelays.Length && now - _requestedAt >= RetryDelays[_nextRetry])
            _nextRetry++;
        return true;
    }
}
