namespace Miaosoft.Sim.Combat.Skill.EventData;

public enum UnitLevelUpFailure
{
    None,
    StaleLevel,
    NotAnIncrease,
    OutsideLevelRange
}

public sealed record UnitLevelUpRequestEvent : Event, IPropagateHandlerPriority
{
    public required Unit Unit { get; init; }
    public required int ExpectedLevel { get; init; }
    public required int NewLevel { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public EventReceiver? CompletionReceiver { get; init; }
}

public sealed record UnitLevelUpEvent : Event, IPropagateHandlerPriority
{
    private sealed class PublicationState
    {
        public bool Dispatched { get; set; }
    }

    private readonly PublicationState publication = new();
    public required Unit Unit { get; init; }
    public required Guid OperationId { get; init; }
    public required int OldLevel { get; init; }
    public required int NewLevel { get; init; }

    public override IEnumerable<IEvent> Dispatch()
    {
        if (publication.Dispatched)
            throw new InvalidOperationException("The level increase has already been published.");
        if (OperationId == Guid.Empty || OldLevel < 1 || NewLevel <= OldLevel ||
            CommittedOperationId != OperationId || !ReferenceEquals(Receiver, Unit.EventReceiver) ||
            !ReferenceEquals(Unit.EventReceiver.Context, Context) || !Context.ContainsReceiver(Unit.EventReceiver))
            throw new InvalidOperationException("A level increase publication requires its committed operation and unit.");
        publication.Dispatched = true;
        if (!Context.HasIndexedEvent<UnitLevelUpEvent>())
            return base.Dispatch();
        AbilityTriggerChain chain = Causality as AbilityTriggerChain ??
            throw new InvalidOperationException("The level increase requires the context's trigger chain.");
        var (local, other) = chain.Router.PlanUnitLevelUp(this);
        return Unit.EventReceiver.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
    }
}

public sealed record UnitLevelUpResultEvent : Event, IPropagateHandlerPriority
{
    public required Unit Unit { get; init; }
    public required Guid OperationId { get; init; }
    public required int OldLevel { get; init; }
    public required int NewLevel { get; init; }
    public required UnitLevelUpFailure Failure { get; init; }
    public bool Success => Failure == UnitLevelUpFailure.None;
}
