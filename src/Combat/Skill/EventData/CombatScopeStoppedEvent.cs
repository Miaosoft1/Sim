namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record CombatScopeStoppedEvent : Event
{
    public required Unit Unit { get; init; }
    public required EventScope StoppedScope { get; init; }
}
