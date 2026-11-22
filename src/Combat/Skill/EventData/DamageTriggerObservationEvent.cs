namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DamageTriggerObservationEvent : Event
{
    public required DamageEvent Damage { get; init; }
    public required Unit Holder { get; init; }
}
