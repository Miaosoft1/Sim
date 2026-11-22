namespace Miaosoft.Sim.Combat.Skill.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private long ticks;

    public int? FailTimerCreationAt { get; set; }
    public int TimerCreations { get; private set; }
    public int DisposedTimers { get; private set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TimerCreations++;
        if (TimerCreations == FailTimerCreationAt)
            throw new InvalidOperationException("Timer creation failed.");
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
        ticks = checked(ticks + amount.Ticks);
        foreach (ManualTimer timer in timers.ToArray())
            timer.FireIfDue(ticks);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long due = long.MaxValue;
        private long period;
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan interval)
        {
            if (disposed) return false;
            due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner.ticks + dueTime.Ticks);
            period = interval.Ticks;
            return true;
        }

        public void FireIfDue(long now)
        {
            if (disposed || due > now) return;
            due = period > 0 ? checked(now + period) : long.MaxValue;
            callback(state);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.DisposedTimers++;
            owner.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
