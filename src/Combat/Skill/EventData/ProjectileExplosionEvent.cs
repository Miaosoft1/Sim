using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class ProjectileExplosionEvent : ProjectileHitEvent
{
    private sealed class ExplosionPublicationState
    {
        public bool Dispatched { get; set; }
    }

    private readonly ExplosionPublicationState publication = new();

    public override IEnumerable<IEvent> Dispatch()
    {
        if (!Context.HasIndexedEvent<ProjectileExplosionEvent>())
            return base.Dispatch();
        if (publication.Dispatched)
            throw new InvalidOperationException("The projectile explosion has already been published.");
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(AbilityId);
        if (!ReferenceEquals(Receiver, Source.EventReceiver) ||
            !ReferenceEquals(Source.EventReceiver.Context, Context) || Target != null)
            throw new InvalidOperationException("The projectile explosion has an invalid source or target.");
        AbilityTriggerChain chain = TriggerChain ??
            throw new InvalidOperationException("The explosion requires the context's trigger chain.");
        publication.Dispatched = true;
        var (local, other) = chain.Router.PlanExplosion(this);
        return Source.EventReceiver.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
    }
}
