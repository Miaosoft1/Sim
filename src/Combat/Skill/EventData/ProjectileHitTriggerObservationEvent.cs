namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ProjectileHitTriggerObservationEvent : Event
{
    public required ProjectileHitEvent Hit { get; init; }
    public required Unit Holder { get; init; }
}
