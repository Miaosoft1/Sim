namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitRetirementRequestEvent : Event
{
    public required Unit Unit { get; init; }
}

public sealed record UnitRetiredEvent : Event
{
    public required Unit Unit { get; init; }
}
