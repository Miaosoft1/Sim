namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DeathCommitEvent : Event, IPropagateHandlerPriority
{
    public override int Priority { get; set; } = CombatEventPriority.DeathCommit;
    public required DeathEvent Attempt { get; init; }
    public bool ReapplyDeferredDamage { get; init; }
}
