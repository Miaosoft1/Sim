namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitLevelUpTriggerObservationEvent : Event
{
    public required UnitLevelUpEvent Upgrade { get; init; }
    public required Unit Holder { get; init; }
}
