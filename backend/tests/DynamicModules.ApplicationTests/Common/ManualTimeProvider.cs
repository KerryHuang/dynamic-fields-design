namespace DynamicModules.ApplicationTests.Common;

/// <summary>可手動快轉、且 CreateTimer（含 CancellationTokenSource 逾時）會隨 Advance 觸發的 TimeProvider。</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        ManualTimer[] due;
        lock (_timers) due = _timers.Where(t => t.Due is { } d && d <= _now).ToArray();
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            _period = period;
            return true;
        }

        public void Fire()
        {
            Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? owner._now + _period : null;
            callback(state);
        }

        public void Dispose() { Due = null; lock (owner._timers) owner._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
