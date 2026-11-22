namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitReviveRequestEvent : Event
{
    public required Unit Unit { get; init; }
    public int? RestoreLifePoint { get; init; }
    public EventReceiver? CompletionReceiver { get; init; }
}

public sealed record UnitRevivedEvent : Event
{
    public required Unit Unit { get; init; }
    public int PreviousLifePoint { get; init; }
}

public sealed record UnitReviveSkippedEvent : Event
{
    public required Unit Unit { get; init; }
}

public enum UnitReviveRejectionReason
{
    Retired
}

public sealed record UnitReviveRejectedEvent : Event
{
    public required Unit Unit { get; init; }
    public required UnitReviveRejectionReason Reason { get; init; }
}
