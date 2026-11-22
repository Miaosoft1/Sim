namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DeferredDamageReplayCompletedEvent : Event, IPropagateHandlerPriority
{
    public required DeferredCombatImpacts Settlement { get; init; }
    public required DamageEvent Damage { get; init; }
}
