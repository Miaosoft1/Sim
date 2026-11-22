namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class UnitActionGateRequestEvent : Event
{
    public required Unit Source { get; init; }
    public required Event Origin { get; init; }
    public required Guid CommandId { get; init; }
    public Guid ActivationId { get; init; } = Guid.NewGuid();
    public required string AbilityId { get; init; }
    public required CancellationTokenSource ActionCancellation { get; init; }
    public long? ControlVersion { get; init; }
    public CommandGateManager? AbilityGate { get; init; }
    public TimeSpan? Windup { get; init; }
    public bool CheckRestrictions { get; init; } = true;
    public bool OccupiesChannel { get; init; } = true;
    public CastingBehavior? Casting { get; init; }
    public AbilityTriggerChain? TriggerChain
    {
        get => Causality as AbilityTriggerChain;
        init => Causality = value;
    }
    public CapturedHandlerTarget? AbilityTarget { get; init; }
}

public record class UnitActionGateResultEvent : Event, ITargetedHandlerEvent
{
    public required UnitActionGateRequestEvent Request { get; init; }
    public required CommandGateCheckResult Result { get; init; }
    public CapturedHandlerTarget? TargetedHandler => Request.AbilityTarget;
}

public record class UnitActionGateFlushEvent : Event;
