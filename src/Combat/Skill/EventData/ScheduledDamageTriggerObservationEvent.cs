namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ScheduledDamageTriggerObservationEvent : Event
{
    public required ScheduledDamageTickEvent Tick { get; init; }
    public required Unit Holder { get; init; }
}
