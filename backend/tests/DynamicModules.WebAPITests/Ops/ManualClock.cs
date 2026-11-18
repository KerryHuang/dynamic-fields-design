namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 手動快轉的 TimeProvider，計時器也跟著它走（PeriodicTimer、CancellationTokenSource(delay, time) 都經 CreateTimer）：
/// 只有 <see cref="Advance"/> 會觸發計時器，排程器測試不必真的等待。
/// </summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>往前快轉；途中到期的計時器依時間先後同步觸發（週期型的每個週期都觸發一次）。</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_sync) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_sync)
            {
                next = _timers.Where(t => t.DueAt is { } d && d <= target).MinBy(t => t.DueAt);
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.DueAt!.Value;
                next.DueAt = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._sync)
            {
                if (!clock._timers.Contains(this)) clock._timers.Add(this);
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }
            return true;
        }

        public void Dispose()
        {
            lock (clock._sync) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
