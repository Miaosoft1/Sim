using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ProjectileHitPublicationEvent : Event
{
    public required ProjectileHitEvent Hit { get; init; }

    public static ProjectileHitPublicationEvent FromHit(ProjectileHitEvent hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (hit.GetType() != typeof(ProjectileHitEvent) || hit.IsDelivery)
            throw new InvalidOperationException("Only a new projectile hit can be published.");
        ArgumentNullException.ThrowIfNull(hit.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(hit.AbilityId);
        Context context = hit.Context ?? throw new InvalidOperationException("The hit requires a context.");
        if (!ReferenceEquals(hit.Source.EventReceiver.Context, context) ||
            hit.Target != null && !ReferenceEquals(hit.Target.EventReceiver.Context, context))
            throw new InvalidOperationException("A projectile hit cannot cross contexts.");
        if (!ReferenceEquals(hit.Receiver, hit.Source.EventReceiver))
            throw new InvalidOperationException("The original hit must use the source receiver.");
        if (hit.Publication.Claimed)
            throw new InvalidOperationException("The projectile hit has already been published.");
        hit.Publication.Claimed = true;
        return context.Factory.NewEvent<ProjectileHitPublicationEvent>() with
        {
            Hit = hit, Receiver = hit.Source.EventReceiver, Create = hit.Create, Delay = hit.Delay,
            Priority = hit.Priority, Causality = hit.Causality, IsCausalityDetached = hit.IsCausalityDetached,
            Scope = hit.Scope, IsScopeDetached = hit.IsScopeDetached,
            CancellationTokenSource = hit.CancellationTokenSource,
            CorrelationId = hit.CorrelationId, CommittedOperationId = hit.CommittedOperationId
        };
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        if (!Hit.Publication.Claimed || Hit.Publication.Dispatched)
            throw new InvalidOperationException("The projectile hit publication has already been dispatched.");
        if (!ReferenceEquals(Hit.Context, Context) ||
            !ReferenceEquals(Hit.Source.EventReceiver.Context, Context) ||
            Hit.Target != null && !ReferenceEquals(Hit.Target.EventReceiver.Context, Context) ||
            !ReferenceEquals(Hit.Receiver, Hit.Source.EventReceiver) ||
            !ReferenceEquals(Receiver, Hit.Source.EventReceiver))
            throw new InvalidOperationException("The projectile hit publication is inconsistent.");
        Hit.Publication.Dispatched = true;
        if (Hit.Causality != null && !ReferenceEquals(Hit.Causality, Causality))
            throw new InvalidOperationException("The projectile hit publication has inconsistent causality.");
        Hit.Causality = Causality;
        var sourceDelivery = Hit with
        {
            Receiver = Hit.Source.EventReceiver, IsDelivery = true,
            Create = Context.ContextTimeSpan, Delay = TimeSpan.Zero, IsCausalityDetached = false
        };
        ProjectileHitEvent? targetDelivery = null;
        if (Hit.Target != null && !ReferenceEquals(Hit.Target.EventReceiver, Hit.Source.EventReceiver))
            targetDelivery = sourceDelivery with { Receiver = Hit.Target.EventReceiver };
        Hit.Publication.SetDeliveries(sourceDelivery, targetDelivery);
        foreach (IEvent item in base.Dispatch())
            yield return item;
        yield return sourceDelivery;
        if (targetDelivery != null) yield return targetDelivery;
        if (Context.HasIndexedEvent<ProjectileHitEvent>())
            yield return Context.Factory.NewEvent<IndexedObservationDispatchEvent<ProjectileHitEvent>>() with
            {
                Publication = Hit.Publication, Receiver = Receiver, Priority = Priority, Causality = Causality,
                Scope = Scope, CancellationTokenSource = CancellationTokenSource,
                CorrelationId = CorrelationId, CommittedOperationId = CommittedOperationId
            };
    }
}
