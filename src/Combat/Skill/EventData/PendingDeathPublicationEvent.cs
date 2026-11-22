using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record PendingDeathPublicationEvent : Event
{
    public required DeathEvent Attempt { get; init; }

    public static PendingDeathPublicationEvent FromAttempt(DeathEvent attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.IsDelivery)
            throw new InvalidOperationException("Only a new death attempt can be published.");
        ArgumentNullException.ThrowIfNull(attempt.Victim);
        ArgumentNullException.ThrowIfNull(attempt.DamageAbility);
        Context context = attempt.Context ?? throw new InvalidOperationException("The death attempt requires a context.");
        if (!ReferenceEquals(attempt.Victim.EventReceiver.Context, context) ||
            attempt.Killer != null && !ReferenceEquals(attempt.Killer.EventReceiver.Context, context))
            throw new InvalidOperationException("A death attempt cannot cross contexts.");
        if (!ReferenceEquals(attempt.Receiver, attempt.Victim.EventReceiver))
            throw new InvalidOperationException("The original death attempt must use the victim receiver.");
        if (attempt.Publication.Claimed)
            throw new InvalidOperationException("The death attempt has already been published.");
        attempt.Publication.Claimed = true;
        return context.Factory.NewEvent<PendingDeathPublicationEvent>() with
        {
            Attempt = attempt, Receiver = attempt.Victim.EventReceiver, Create = attempt.Create, Delay = attempt.Delay,
            Priority = attempt.Priority, Causality = attempt.Causality,
            IsCausalityDetached = attempt.IsCausalityDetached, Scope = attempt.Scope,
            IsScopeDetached = attempt.IsScopeDetached, CancellationTokenSource = attempt.CancellationTokenSource,
            CorrelationId = attempt.CorrelationId, CommittedOperationId = attempt.CommittedOperationId
        };
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        ArgumentNullException.ThrowIfNull(Attempt.Victim);
        if (!Attempt.Publication.Claimed || Attempt.Publication.Dispatched)
            throw new InvalidOperationException("The death attempt publication has already been dispatched.");
        if (!ReferenceEquals(Attempt.Context, Context) ||
            !ReferenceEquals(Attempt.Victim.EventReceiver.Context, Context) ||
            Attempt.Killer != null && !ReferenceEquals(Attempt.Killer.EventReceiver.Context, Context) ||
            !ReferenceEquals(Attempt.Receiver, Attempt.Victim.EventReceiver) ||
            !ReferenceEquals(Receiver, Attempt.Victim.EventReceiver))
            throw new InvalidOperationException("The death attempt publication is inconsistent.");
        Attempt.Publication.Dispatched = true;
        if (Attempt.Causality != null && !ReferenceEquals(Attempt.Causality, Causality))
            throw new InvalidOperationException("The death attempt publication has inconsistent causality.");
        Attempt.Causality = Causality;
        var victimDelivery = Attempt with
        {
            Receiver = Attempt.Victim.EventReceiver, IsDelivery = true,
            Create = Context.ContextTimeSpan, Delay = TimeSpan.Zero, IsCausalityDetached = false
        };
        DeathEvent? killerDelivery = null;
        if (Attempt.Killer != null && !ReferenceEquals(Attempt.Killer.EventReceiver, Attempt.Victim.EventReceiver))
            killerDelivery = victimDelivery with { Receiver = Attempt.Killer.EventReceiver };
        Attempt.Publication.SetDeliveries(victimDelivery, killerDelivery);
        foreach (IEvent item in base.Dispatch()) yield return item;
        yield return victimDelivery;
        if (killerDelivery != null) yield return killerDelivery;
        if (Context.HasIndexedEvent<DeathEvent>())
            yield return Context.Factory.NewEvent<IndexedObservationDispatchEvent<DeathEvent>>() with
            {
                Publication = Attempt.Publication, Receiver = Receiver, Priority = Priority, Causality = Causality,
                Scope = Scope, CancellationTokenSource = CancellationTokenSource,
                CorrelationId = CorrelationId, CommittedOperationId = CommittedOperationId
            };
    }
}
