using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ScheduledDamageTickEvent : Event
{
    private sealed class PublicationState
    {
        public bool Dispatched { get; set; }
    }

    private readonly PublicationState publication = new();
    public required Guid CommandEventId { get; init; }
    public required Unit? Source { get; init; }
    public required Unit Target { get; init; }
    public required DamageProfile Damage { get; init; }
    public StatusApplicationOrigin? ApplicationOrigin { get; init; }
    internal PeriodicEffectHandle? PeriodicEffect { get; init; }
    internal bool IsFinalTick { get; init; }
    public AbilityTriggerChain? TriggerChain
    {
        get => Causality as AbilityTriggerChain;
        set => Causality = value;
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        if (!Context.HasIndexedEvent<ScheduledDamageTickEvent>())
            return base.Dispatch();
        if (publication.Dispatched)
            throw new InvalidOperationException("The scheduled damage tick has already been published.");
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(Damage);
        if (!ReferenceEquals(Receiver, Target.EventReceiver) ||
            !ReferenceEquals(Target.EventReceiver.Context, Context) ||
            Source != null && !ReferenceEquals(Source.EventReceiver.Context, Context))
            throw new InvalidOperationException("The scheduled damage source or target does not belong to this context.");
        AbilityTriggerChain chain = TriggerChain ??
            throw new InvalidOperationException("The scheduled damage tick requires the context's trigger chain.");
        publication.Dispatched = true;
        var (local, other) = chain.Router.PlanScheduledDamage(this);
        return Target.EventReceiver.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
    }
}
