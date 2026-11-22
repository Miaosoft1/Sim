namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class CastStartedEvent : Event
{
    public required Unit Source { get; init; }
    public required ActiveCast Cast { get; init; }
}

public record class CastFinishedEvent : Event
{
    public required Unit Source { get; init; }
    public required Guid CommandId { get; init; }
    public required Guid ActivationId { get; init; }
}

public record class CastChannelStartedEvent : Event, IPropagateHandlerPriority
{
    public required UnitActionGateRequestEvent Request { get; init; }
    public override int Priority { get; set; } = int.MinValue;
}

public record class CastInterruptRequestEvent : Event
{
    public required Unit Source { get; init; }
}

public record class CastCancellationRequestedEvent : Event, IPropagateHandlerPriority
{
    public required Unit Source { get; init; }
    public required Guid CommandId { get; init; }
    public required Guid ActivationId { get; init; }
    public override int Priority { get; set; } = int.MinValue;
}

public record class CastInterruptedEvent : Event
{
    public required Unit Source { get; init; }
    public required ActiveCast Cast { get; init; }
    public bool ControlChanged { get; init; }
}

public record class UnitActionResultEvent : Event
{
    public required Unit Source { get; init; }
    public required string Message { get; init; }
    public bool Success { get; init; }
}
