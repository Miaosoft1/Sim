namespace Miaosoft.Sim.Combat.Skill.Ability;

internal sealed class AbilityChargeState
{
    private readonly AbilityChargeConfiguration configuration;
    private readonly SortedDictionary<TimeSpan, int> recoveries = [];
    private int available;

    public AbilityChargeState(AbilityChargeConfiguration configuration, TimeSpan enteredAt)
    {
        this.configuration = configuration;
        available = configuration.Initial;
        int missing = configuration.Maximum - available;
        if (missing > 0 && configuration.RecoveryInterval is { } interval)
        {
            TimeSpan deadline = enteredAt.Add(interval);
            recoveries.Add(deadline, configuration.RecoveryMode == AbilityChargeRecoveryMode.Serial ? 1 : missing);
        }
    }

    private AbilityChargeState(AbilityChargeState source)
    {
        configuration = source.configuration;
        available = source.available;
        foreach (var recovery in source.recoveries)
            recoveries.Add(recovery.Key, recovery.Value);
    }

    public AbilityChargeState Copy() => new(this);

    public int Available => available;
    public TimeSpan? NextRecovery => recoveries.Count == 0 ? null : recoveries.First().Key;

    public AbilityChargeSnapshot Snapshot() => new(
        available, configuration.Maximum, configuration.RecoveryMode,
        Array.AsReadOnly(recoveries.Select(entry => new AbilityChargeRecoveryDeadline(entry.Key, entry.Value)).ToArray()));

    public bool Advance(TimeSpan now)
    {
        if (recoveries.Count == 0 || recoveries.First().Key > now) return false;
        if (configuration.RecoveryMode == AbilityChargeRecoveryMode.Serial)
        {
            TimeSpan deadline = recoveries.First().Key;
            TimeSpan interval = configuration.RecoveryInterval!.Value;
            long elapsedPeriods = (now.Ticks - deadline.Ticks) / interval.Ticks;
            int restored = (int)Math.Min(configuration.Maximum - (long)available, elapsedPeriods + 1);
            int remaining = configuration.Maximum - available - restored;
            TimeSpan? next = remaining > 0
                ? TimeSpan.FromTicks(checked(deadline.Ticks + checked(interval.Ticks * (long)restored)))
                : null;
            recoveries.Clear();
            available += restored;
            if (next.HasValue) recoveries.Add(next.Value, 1);
            return true;
        }

        while (recoveries.Count > 0 && recoveries.First().Key <= now)
        {
            KeyValuePair<TimeSpan, int> due = recoveries.First();
            available = checked(available + due.Value);
            recoveries.Remove(due.Key);
        }
        return true;
    }

    public TimeSpan? PrepareConsumption(TimeSpan now)
    {
        if (available <= 0) throw new InvalidOperationException("The ability has no charges.");
        if (configuration.RecoveryInterval is not { } interval) return null;
        if (configuration.RecoveryMode == AbilityChargeRecoveryMode.Serial && recoveries.Count > 0)
            return null;
        return now.Add(interval);
    }

    public void Consume(TimeSpan? newDeadline)
    {
        available--;
        if (newDeadline is { } deadline)
            recoveries[deadline] = recoveries.TryGetValue(deadline, out int count) ? checked(count + 1) : 1;
    }

    public int Restore(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        int restored = Math.Min(amount, configuration.Maximum - available);
        if (restored == 0) return 0;
        available += restored;
        if (configuration.RecoveryMode == AbilityChargeRecoveryMode.Serial)
        {
            if (available == configuration.Maximum) recoveries.Clear();
        }
        else if (configuration.RecoveryMode == AbilityChargeRecoveryMode.Parallel)
        {
            int remaining = restored;
            while (remaining > 0)
            {
                KeyValuePair<TimeSpan, int> latest = recoveries.Last();
                int removed = Math.Min(remaining, latest.Value);
                if (removed == latest.Value) recoveries.Remove(latest.Key);
                else recoveries[latest.Key] = latest.Value - removed;
                remaining -= removed;
            }
        }
        return restored;
    }
}
