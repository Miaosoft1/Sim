using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DamagePublicationEvent : Event
{
    private sealed class PublicationIdentity
    {
        public DamageEvent? Damage { get; set; }
    }

    private readonly PublicationIdentity identity = new();
    public required DamageEvent Damage { get; init; }
    internal DeferredDamageEvent? Replay { get; init; }

    internal static DamagePublicationEvent FromRequest(DamageRequestEvent request)
    {
        var publication = request.Context.Factory.NewEvent<DamagePublicationEvent>() with
        {
            Damage = request.Damage, Receiver = request.Receiver,
            Priority = request.Priority, Causality = request.Causality,
            Scope = request.Scope, IsScopeDetached = request.Scope == null,
            CancellationTokenSource = request.CancellationTokenSource, CorrelationId = request.CorrelationId,
            CommittedOperationId = request.CommittedOperationId
        };
        return Authorize(publication);
    }

    internal static DamagePublicationEvent ForReplay(DeferredDamageEvent replay)
    {
        var publication = replay.Context.Factory.NewEvent<DamagePublicationEvent>() with
        {
            Damage = replay.Damage, Replay = replay, Receiver = replay.Receiver,
            Priority = replay.Damage.Priority, Causality = replay.Damage.Causality,
            Scope = replay.Scope ?? replay.Damage.PublicationScope,
            IsScopeDetached = replay.Scope == null && replay.Damage.PublicationScope == null,
            CancellationTokenSource = replay.Damage.CancellationTokenSource,
            CorrelationId = replay.Damage.CorrelationId,
            CommittedOperationId = replay.Damage.CommittedOperationId
        };
        return Authorize(publication);
    }

    private static DamagePublicationEvent Authorize(DamagePublicationEvent publication)
    {
        if (publication.Damage.PublicationIdentity != null)
            throw new InvalidOperationException("The damage already has a publication.");
        publication.identity.Damage = publication.Damage;
        publication.Damage.PublicationIdentity = publication.identity;
        return publication;
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        Unit target = Damage.Target ?? throw new InvalidOperationException("Damage requires a target.");
        EventReceiver primary = (Damage.Source ?? target).EventReceiver;
        if (!ReferenceEquals(identity.Damage, Damage) ||
            !ReferenceEquals(Damage.PublicationIdentity, identity) ||
            !Damage.Publication.Claimed || Damage.Publication.Dispatched ||
            !ReferenceEquals(Damage.Context, Context) || !ReferenceEquals(Receiver, primary) ||
            !ReferenceEquals(Damage.Receiver, primary) || !ReferenceEquals(primary.Context, Context) ||
            !ReferenceEquals(target.EventReceiver.Context, Context) || !Damage.HasDamage)
            throw new InvalidOperationException("The damage publication is invalid or already dispatched.");
        if (Damage.Causality != null && !ReferenceEquals(Damage.Causality, Causality))
            throw new InvalidOperationException("The damage publication has inconsistent causality.");
        Damage.Causality = Causality;
        if (Damage.CommittedOperationId.HasValue && Damage.CommittedOperationId != CommittedOperationId)
            throw new InvalidOperationException("The damage publication has inconsistent commitment.");
        Damage.CommittedOperationId = CommittedOperationId;
        Damage.CorrelationId = CorrelationId;
        Damage.Publication.Dispatched = true;
        var first = Damage with
        {
            Receiver = primary, IsDelivery = true, SettlementReplayId = Replay?.Settlement.Id,
            Create = Context.ContextTimeSpan, Delay = TimeSpan.Zero, IsCausalityDetached = false,
            Scope = Replay?.Scope ?? Damage.PublicationScope
        };
        DamageEvent? second = null;
        if (!ReferenceEquals(primary, target.EventReceiver))
            second = first with { Receiver = target.EventReceiver };
        Damage.Publication.SetDeliveries(first, second);
        foreach (IEvent item in base.Dispatch()) yield return item;
        yield return first;
        if (second != null) yield return second;
        if (Context.HasIndexedEvent<DamageEvent>())
            yield return Context.Factory.NewEvent<IndexedObservationDispatchEvent<DamageEvent>>() with
            {
                Publication = Damage.Publication, Receiver = Receiver, Priority = Priority, Causality = Causality,
                Scope = Replay?.Scope ?? Damage.PublicationScope, CancellationTokenSource = Damage.CancellationTokenSource,
                CorrelationId = Damage.CorrelationId, CommittedOperationId = Damage.CommittedOperationId
            };
        if (Replay != null)
            yield return Replay.CreateCompletionBarrier();
    }
}
