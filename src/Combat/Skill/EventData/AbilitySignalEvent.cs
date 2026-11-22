namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record AbilitySignalEvent : Event, IPropagateHandlerPriority
{
    private sealed class PublicationState
    {
        public bool Dispatched { get; set; }
    }

    private readonly PublicationState publication = new();
    public required string SignalId { get; init; }
    public required Guid CommandEventId { get; init; }
    public required Unit? Source { get; init; }
    public required Unit Target { get; init; }
    public AbilityRuntimeSnapshot? AbilitySnapshot { get; init; }
    public EventReceiver? CompletionReceiver { get; init; }
    public AbilityTriggerChain? TriggerChain
    {
        get => Causality as AbilityTriggerChain;
        set => Causality = value;
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        if (!Context.HasIndexedEvent<AbilitySignalEvent>())
            return base.Dispatch();
        if (publication.Dispatched)
            throw new InvalidOperationException("The signal has already been published.");
        ArgumentException.ThrowIfNullOrWhiteSpace(SignalId);
        ArgumentNullException.ThrowIfNull(Target);
        EventReceiver primary = (Source ?? Target).EventReceiver;
        if (!ReferenceEquals(Receiver, primary) || !ReferenceEquals(primary.Context, Context) ||
            !ReferenceEquals(Target.EventReceiver.Context, Context) ||
            CompletionReceiver != null && !ReferenceEquals(CompletionReceiver.Context, Context))
            throw new InvalidOperationException("The signal source, target or completion receiver does not belong to this context.");
        AbilityTriggerChain chain = TriggerChain ??
            throw new InvalidOperationException("The signal requires the context's trigger chain.");
        publication.Dispatched = true;
        var (local, other) = chain.Router.PlanSignal(this);
        return primary.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
    }
}
