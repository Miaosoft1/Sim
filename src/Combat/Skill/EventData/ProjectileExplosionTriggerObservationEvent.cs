namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ProjectileExplosionTriggerObservationEvent : Event
{
    public required ProjectileExplosionEvent Explosion { get; init; }
    public required Unit Holder { get; init; }
}
