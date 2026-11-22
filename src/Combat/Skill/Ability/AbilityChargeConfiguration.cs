namespace Miaosoft.Sim.Combat.Skill.Ability;

public enum AbilityChargeRecoveryMode
{
    Serial,
    Parallel
}

public sealed record AbilityChargeConfiguration
{
    public AbilityChargeConfiguration(int maximum, int initial,
        TimeSpan? recoveryInterval = null, AbilityChargeRecoveryMode? recoveryMode = null)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (initial < 0 || initial > maximum) throw new ArgumentOutOfRangeException(nameof(initial));
        if (recoveryInterval.HasValue != recoveryMode.HasValue)
            throw new ArgumentException("A recovery interval and mode must be configured together.");
        if (recoveryInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryInterval));
        if (recoveryMode is { } mode && !Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(recoveryMode));
        Maximum = maximum;
        Initial = initial;
        RecoveryInterval = recoveryInterval;
        RecoveryMode = recoveryMode;
    }

    public int Maximum { get; }
    public int Initial { get; }
    public TimeSpan? RecoveryInterval { get; }
    public AbilityChargeRecoveryMode? RecoveryMode { get; }
}

public sealed record AbilityChargeRecoveryDeadline(TimeSpan At, int Count);

public sealed record AbilityChargeSnapshot(
    int Available, int Maximum, AbilityChargeRecoveryMode? RecoveryMode,
    IReadOnlyList<AbilityChargeRecoveryDeadline> Recoveries);
