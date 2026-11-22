using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DeathResolvedEvent : Event, IPropagateHandlerPriority
{
    private sealed class PublicationState
    {
        public bool Dispatched { get; set; }
    }

    private readonly PublicationState publication = new();
    public override int Priority { get; set; } = CombatEventPriority.DeathResolved;
    public required Unit Victim { get; init; }
    public Guid DeathId { get; init; } = Guid.NewGuid();
    public Unit? Killer { get; init; }
    public DamageProfile? DamageAbility { get; init; }
    public StatusApplicationOrigin? ApplicationOrigin { get; init; }
    public Map.MapObject? SourceObject { get; init; }
    public Map.MapObject? TargetObject { get; init; }

    public override IEnumerable<IEvent> Dispatch()
    {
        if (!Context.HasIndexedEvent<DeathResolvedEvent>())
            return base.Dispatch();
        if (publication.Dispatched)
            throw new InvalidOperationException("The confirmed death has already been published.");
        ArgumentNullException.ThrowIfNull(Victim);
        if (!ReferenceEquals(Receiver, Victim.EventReceiver) ||
            !ReferenceEquals(Victim.EventReceiver.Context, Context) ||
            Killer != null && !ReferenceEquals(Killer.EventReceiver.Context, Context))
            throw new InvalidOperationException("The confirmed death has an invalid victim or killer.");
        AbilityTriggerChain chain = Causality as AbilityTriggerChain ??
            throw new InvalidOperationException("The confirmed death requires the context's trigger chain.");
        publication.Dispatched = true;
        var (local, other) = chain.Router.PlanConfirmedDeath(this);
        return Victim.EventReceiver.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
    }
}
