namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ScheduledHealTriggerObservationEvent : Event
{
    public required ScheduledHealTickEvent Tick { get; init; }
    public required Unit Holder { get; init; }
}
