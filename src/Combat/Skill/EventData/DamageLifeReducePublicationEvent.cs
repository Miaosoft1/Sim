using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DamageLifeReducePublicationEvent : Event
{
    public required DamageLifeReduceEvent Reduction { get; init; }

    public static DamageLifeReducePublicationEvent FromReduction(DamageLifeReduceEvent reduction)
    {
        ArgumentNullException.ThrowIfNull(reduction);
        if (reduction.IsDelivery)
            throw new InvalidOperationException("Only a new life reduction can be published.");
        Unit target = reduction.Target ??
            throw new InvalidOperationException("A life reduction requires a target.");
        Context context = reduction.Context ?? throw new InvalidOperationException("The life reduction requires a context.");
        if (!ReferenceEquals(target.EventReceiver.Context, context) ||
            reduction.Source != null && !ReferenceEquals(reduction.Source.EventReceiver.Context, context))
            throw new InvalidOperationException("A life reduction cannot cross contexts.");
        if (!ReferenceEquals(reduction.Receiver, target.EventReceiver))
            throw new InvalidOperationException("The original life reduction must use the target receiver.");
        if (reduction.Publication.Claimed)
            throw new InvalidOperationException("The life reduction has already been published.");
        reduction.Publication.Claimed = true;
        return context.Factory.NewEvent<DamageLifeReducePublicationEvent>() with
        {
            Reduction = reduction, Receiver = target.EventReceiver, Create = reduction.Create, Delay = reduction.Delay,
            Priority = reduction.Priority, Causality = reduction.Causality,
            IsCausalityDetached = reduction.IsCausalityDetached, Scope = reduction.Scope,
            IsScopeDetached = reduction.IsScopeDetached, CancellationTokenSource = reduction.CancellationTokenSource,
            CorrelationId = reduction.CorrelationId, CommittedOperationId = reduction.CommittedOperationId
        };
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        Unit target = Reduction.Target ??
            throw new InvalidOperationException("The life reduction requires a target.");
        if (!Reduction.Publication.Claimed || Reduction.Publication.Dispatched)
            throw new InvalidOperationException("The life reduction publication has already been dispatched.");
        if (!ReferenceEquals(Reduction.Context, Context) ||
            !ReferenceEquals(target.EventReceiver.Context, Context) ||
            Reduction.Source != null && !ReferenceEquals(Reduction.Source.EventReceiver.Context, Context) ||
            !ReferenceEquals(Reduction.Receiver, target.EventReceiver) || !ReferenceEquals(Receiver, target.EventReceiver))
            throw new InvalidOperationException("The life reduction publication is inconsistent.");
        Reduction.Publication.Dispatched = true;
        if (Reduction.Causality != null && !ReferenceEquals(Reduction.Causality, Causality))
            throw new InvalidOperationException("The life reduction publication has inconsistent causality.");
        Reduction.Causality = Causality;
        var targetDelivery = Reduction with
        {
            Receiver = target.EventReceiver, IsDelivery = true,
            Create = Context.ContextTimeSpan, Delay = TimeSpan.Zero, IsCausalityDetached = false
        };
        DamageLifeReduceEvent? sourceDelivery = null;
        if (Reduction.Source != null && !ReferenceEquals(Reduction.Source.EventReceiver, target.EventReceiver))
            sourceDelivery = targetDelivery with { Receiver = Reduction.Source.EventReceiver };
        Reduction.Publication.SetDeliveries(targetDelivery, sourceDelivery);
        foreach (IEvent item in base.Dispatch()) yield return item;
        yield return targetDelivery;
        if (sourceDelivery != null) yield return sourceDelivery;
        if (Context.HasIndexedEvent<DamageLifeReduceEvent>())
            yield return Context.Factory.NewEvent<IndexedObservationDispatchEvent<DamageLifeReduceEvent>>() with
            {
                Publication = Reduction.Publication, Receiver = Receiver, Priority = Priority, Causality = Causality,
                Scope = Scope, CancellationTokenSource = CancellationTokenSource,
                CorrelationId = CorrelationId, CommittedOperationId = CommittedOperationId
            };
    }
}
