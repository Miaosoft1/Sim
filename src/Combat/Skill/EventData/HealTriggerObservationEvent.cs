namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record HealTriggerObservationEvent : Event
{
    public required HealEvent Heal { get; init; }
    public required Unit Holder { get; init; }
}
