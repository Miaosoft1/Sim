using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record HealPublicationEvent : Event
{
    public required HealEvent Heal { get; init; }

    public static HealPublicationEvent FromHeal(HealEvent heal)
    {
        ArgumentNullException.ThrowIfNull(heal);
        if (heal.IsDelivery)
            throw new InvalidOperationException("Only a new heal request can be published.");
        ArgumentNullException.ThrowIfNull(heal.Target);
        Context context = heal.Context ?? throw new InvalidOperationException("The heal request requires a context.");
        if (!ReferenceEquals(heal.Target.EventReceiver.Context, context) ||
            heal.Source != null && !ReferenceEquals(heal.Source.EventReceiver.Context, context))
            throw new InvalidOperationException("A heal request cannot cross contexts.");
        if (!ReferenceEquals(heal.Receiver, heal.Target.EventReceiver))
            throw new InvalidOperationException("The original heal request must use the target receiver.");
        if (heal.Publication.Claimed)
            throw new InvalidOperationException("The heal request has already been published.");
        heal.Publication.Claimed = true;
        return context.Factory.NewEvent<HealPublicationEvent>() with
        {
            Heal = heal, Receiver = heal.Target.EventReceiver, Create = heal.Create, Delay = heal.Delay,
            Priority = heal.Priority, Causality = heal.Causality, IsCausalityDetached = heal.IsCausalityDetached,
            Scope = heal.Scope, IsScopeDetached = heal.IsScopeDetached,
            CancellationTokenSource = heal.CancellationTokenSource,
            CorrelationId = heal.CorrelationId, CommittedOperationId = heal.CommittedOperationId
        };
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        if (!Heal.Publication.Claimed || Heal.Publication.Dispatched)
            throw new InvalidOperationException("The heal request publication has already been dispatched.");
        if (!ReferenceEquals(Heal.Context, Context) ||
            !ReferenceEquals(Heal.Target.EventReceiver.Context, Context) ||
            Heal.Source != null && !ReferenceEquals(Heal.Source.EventReceiver.Context, Context) ||
            !ReferenceEquals(Heal.Receiver, Heal.Target.EventReceiver) ||
            !ReferenceEquals(Receiver, Heal.Target.EventReceiver))
            throw new InvalidOperationException("The heal request publication is inconsistent.");
        Heal.Publication.Dispatched = true;
        if (Heal.Causality != null && !ReferenceEquals(Heal.Causality, Causality))
            throw new InvalidOperationException("The heal request publication has inconsistent causality.");
        Heal.Causality = Causality;
        var targetDelivery = Heal with
        {
            IsDelivery = true, Create = Context.ContextTimeSpan,
            Delay = TimeSpan.Zero, IsCausalityDetached = false
        };
        HealEvent? sourceDelivery = null;
        if (Heal.Source != null && !ReferenceEquals(Heal.Source.EventReceiver, Heal.Target.EventReceiver))
            sourceDelivery = targetDelivery with { Receiver = Heal.Source.EventReceiver };
        Heal.Publication.SetDeliveries(targetDelivery, sourceDelivery);
        foreach (IEvent item in base.Dispatch()) yield return item;
        yield return targetDelivery;
        if (sourceDelivery != null) yield return sourceDelivery;
        if (Context.HasIndexedEvent<HealEvent>())
            yield return Context.Factory.NewEvent<IndexedObservationDispatchEvent<HealEvent>>() with
            {
                Publication = Heal.Publication, Receiver = Receiver, Priority = Priority, Causality = Causality,
                Scope = Scope, CancellationTokenSource = CancellationTokenSource,
                CorrelationId = CorrelationId, CommittedOperationId = CommittedOperationId
            };
    }
}
