namespace Miaosoft.Sim.Tests.Support;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ManualTimer> timers = [];
    private long ticks;
    private int timestampReads;
    public int TimestampReadCount => Volatile.Read(ref timestampReads);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp()
    {
        Interlocked.Increment(ref timestampReads);
        lock (sync) return ticks;
    }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
    public int PendingTimerCount
    {
        get { lock (sync) return timers.Count(timer => timer.Due != long.MaxValue); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    public void Advance(TimeSpan amount)
    {
        List<Action> callbacks = [];
        lock (sync)
        {
            ticks += amount.Ticks;
            foreach (var timer in timers.ToArray())
            {
                if (timer.Due > ticks) continue;
                timer.Due = timer.Period > 0 ? ticks + timer.Period : long.MaxValue;
                callbacks.Add(timer.Invoke);
            }
        }
        foreach (Action callback in callbacks) callback();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        public long Period { get; private set; }
        private bool disposed;
        public void Invoke() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                Period = period.Ticks;
                return true;
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
