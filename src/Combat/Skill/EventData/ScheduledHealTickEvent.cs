using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ScheduledHealTickEvent : Event
{
    private sealed class PublicationState
    {
        public bool Dispatched { get; set; }
    }

    private readonly PublicationState publication = new();
    public required Guid CommandEventId { get; init; }
    public required Unit? Source { get; init; }
    public required Unit Target { get; init; }
    public required HealProfile Heal { get; init; }
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
        if (!Context.HasIndexedEvent<ScheduledHealTickEvent>())
            return base.Dispatch();
        if (publication.Dispatched)
            throw new InvalidOperationException("The scheduled heal tick has already been published.");
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(Heal);
        if (!ReferenceEquals(Receiver, Target.EventReceiver) ||
            !ReferenceEquals(Target.EventReceiver.Context, Context) ||
            Source != null && !ReferenceEquals(Source.EventReceiver.Context, Context))
            throw new InvalidOperationException("The scheduled heal source or target does not belong to this context.");
        AbilityTriggerChain chain = TriggerChain ??
            throw new InvalidOperationException("The scheduled heal tick requires the context's trigger chain.");
        publication.Dispatched = true;
        var (local, other) = chain.Router.PlanScheduledHeal(this);
        return Target.EventReceiver.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
    }
}
