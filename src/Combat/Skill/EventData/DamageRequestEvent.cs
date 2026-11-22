using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DamageRequestEvent : Event
{
    private sealed class RequestAttempt
    {
        public bool Processed { get; set; }
        public DamageEvent? Damage { get; set; }
    }

    private readonly RequestAttempt attempt = new();
    public required DamageEvent Damage { get; init; }

    public static DamageRequestEvent FromDamage(DamageEvent damage)
    {
        ArgumentNullException.ThrowIfNull(damage);
        if (damage.IsDelivery || damage.Publication.Claimed)
            throw new InvalidOperationException("Only a new damage request can be submitted.");
        Unit target = damage.Target ?? throw new InvalidOperationException("Damage requires a target.");
        Context context = damage.Context ?? throw new InvalidOperationException("Damage requires a context.");
        EventReceiver primary = (damage.Source ?? target).EventReceiver;
        if (!damage.HasDamage || !ReferenceEquals(damage.Receiver, primary) ||
            !ReferenceEquals(primary.Context, context) || !ReferenceEquals(target.EventReceiver.Context, context))
            throw new InvalidOperationException("The damage source, target or effect is invalid for this context.");
        var request = context.Factory.NewEvent<DamageRequestEvent>() with
        {
            Damage = damage, Receiver = primary, Create = damage.Create, Delay = damage.Delay,
            Priority = damage.Priority, Causality = damage.Causality, IsCausalityDetached = damage.IsCausalityDetached,
            Scope = damage.Scope, IsScopeDetached = damage.IsScopeDetached,
            CancellationTokenSource = damage.CancellationTokenSource, CorrelationId = damage.CorrelationId,
            CommittedOperationId = damage.CommittedOperationId
        };
        request.attempt.Damage = damage;
        damage.RequestIdentity = request.attempt;
        damage.Publication.Claimed = true;
        return request;
    }

    public override IEnumerable<IEvent> Dispatch()
    {
        Unit target = Damage.Target ?? throw new InvalidOperationException("Damage requires a target.");
        EventReceiver primary = (Damage.Source ?? target).EventReceiver;
        if (attempt.Processed || !ReferenceEquals(attempt.Damage, Damage) ||
            !ReferenceEquals(Damage.RequestIdentity, attempt) ||
            !Damage.Publication.Claimed || Damage.Publication.Dispatched ||
            !ReferenceEquals(Damage.Context, Context) || !ReferenceEquals(Receiver, primary) ||
            !ReferenceEquals(Damage.Receiver, primary) || !ReferenceEquals(primary.Context, Context) ||
            !ReferenceEquals(target.EventReceiver.Context, Context) || !Damage.HasDamage)
            throw new InvalidOperationException("The damage request is invalid or already processed.");
        if (Damage.Causality != null && !ReferenceEquals(Damage.Causality, Causality))
            throw new InvalidOperationException("The damage request has inconsistent causality.");
        Damage.Causality = Causality;
        if (Damage.CommittedOperationId.HasValue && Damage.CommittedOperationId != CommittedOperationId)
            throw new InvalidOperationException("The damage request has inconsistent commitment.");
        Damage.CommittedOperationId = CommittedOperationId;
        Damage.CorrelationId = CorrelationId;
        Damage.PublicationScope = Scope;
        attempt.Processed = true;
        if (target.DeferredImpacts?.TryDeferDamage(Damage, EnqueueSequence) == true)
            yield break;
        yield return DamagePublicationEvent.FromRequest(this);
    }
}
