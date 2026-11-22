namespace Miaosoft.Sim.Combat.Skill.EventData;

public enum PeriodicEffectRejectionReason
{
    None,
    Canceled,
    NestingDepth,
    Capacity
}

public abstract record PeriodicEffectApplication
{
    public abstract string EffectId { get; }
    public required int TickCount { get; init; }
    public required TimeSpan TickInterval { get; init; }
    public TimeSpan FirstTickDelay { get; init; }
    public TimeSpan? ExpiresAt { get; init; }
    public PeriodicEffectHandle? Replaces { get; init; }
    internal StatusAbility? StackOwner { get; init; }
}

public sealed record PeriodicDamageApplication : PeriodicEffectApplication
{
    public required DamageProfile Damage { get; init; }
    public override string EffectId => Damage.Id;
}

public sealed record PeriodicHealApplication : PeriodicEffectApplication
{
    public required HealProfile Heal { get; init; }
    public override string EffectId => Heal.Id;
}

public sealed record PeriodicDamageRequestEvent : Event
{
    public required Guid CommandEventId { get; init; }
    public required Unit? Source { get; init; }
    public required Unit Target { get; init; }
    public required DamageProfile Damage { get; init; }
    public required int TickCount { get; init; }
    public required TimeSpan TickInterval { get; init; }
    public TimeSpan FirstTickDelay { get; init; }
    public TimeSpan? ExpiresAt { get; init; }
    public PeriodicEffectHandle? Replaces { get; init; }
    public EventReceiver? CompletionReceiver { get; init; }
}

public sealed record PeriodicEffectStartedEvent : Event
{
    public required Guid CommandEventId { get; init; }
    public required Unit Target { get; init; }
    public required PeriodicEffectHandle Effect { get; init; }
}

public sealed record PeriodicEffectRejectedEvent : Event
{
    public required Guid CommandEventId { get; init; }
    public required Unit Target { get; init; }
    public required string EffectId { get; init; }
    public required PeriodicEffectRejectionReason Reason { get; init; }
}

internal sealed record PeriodicEffectExpiredEvent : Event
{
    public required PeriodicEffectHandle Effect { get; init; }
}

internal sealed record PeriodicEffectCanceledEvent : Event
{
    public required PeriodicEffectHandle Effect { get; init; }
}
