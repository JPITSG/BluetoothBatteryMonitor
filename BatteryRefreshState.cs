namespace BluetoothBatteryMonitor;

// One battery operation per connection, with a wake request that cannot be
// swallowed by an older in-flight read finishing after the request.
internal sealed class BatteryRefreshState
{
    private long _requested;
    private long _completed = -1;
    private Attempt? _inFlight;

    public bool NeedsRefresh(int? batteryLevel, bool characteristicAvailable) =>
        _requested != _completed || !batteryLevel.HasValue || !characteristicAvailable;

    public void Request() => _requested++;

    public Attempt? TryBegin(long generation)
    {
        if (_inFlight?.Generation == generation) return null;
        return _inFlight = new Attempt(generation, _requested);
    }

    public void Complete(Attempt attempt, bool success)
    {
        if (!ReferenceEquals(_inFlight, attempt)) return;
        _inFlight = null;
        _completed = success ? attempt.Request : -1;
    }

    public void Reset()
    {
        _inFlight = null;
        Request();
    }

    internal sealed record Attempt(long Generation, long Request);
}
