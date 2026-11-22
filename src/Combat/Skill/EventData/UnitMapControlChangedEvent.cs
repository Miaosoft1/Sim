namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitMapControlChangedEvent : Event
{
    public required Unit Unit { get; init; }
}
