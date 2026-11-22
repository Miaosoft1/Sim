using System.Collections.Immutable;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill;

internal sealed class PeriodicEffectRegistry : IContextObserver
{
    internal sealed record PreparedEffect(PeriodicEffectApplication Application, PeriodicEffectHandle Handle);
    internal sealed record TargetedEffect(
        Unit Target, PeriodicEffectApplication Application, Guid? StatusApplicationId,
        StatusApplicationOrigin? Origin, IReadOnlySet<Guid> ReplacementStatusApplications);
    internal sealed record StatusChange(
        Unit Target, IReadOnlyList<PeriodicEffectApplication> Additions,
        Guid StatusApplicationId, Guid? ReplacedStatusApplicationId, StatusApplicationOrigin Origin);

    internal sealed class PreparedChanges(
        PeriodicEffectRegistry registry, IReadOnlyList<PreparedEffect> added,
        IReadOnlyList<PeriodicEffectHandle> retired)
    {
        public IReadOnlyList<PreparedEffect> Added { get; } = added;
        public IReadOnlyList<PeriodicEffectHandle> Retired { get; } = retired;

        public void Publish() => Volatile.Write(ref registry.publishedActiveCount, registry.active.Value.Count);

        public IReadOnlyList<Exception> CloseRetired() => Close(Retired);

        public void Abort()
        {
            IReadOnlyList<Exception> failures = Close(Added.Select(entry => entry.Handle));
            if (failures.Count > 0)
                throw new AggregateException("Prepared periodic effects failed to close.", failures);
        }

        public void RollbackAdded()
        {
            if (Retired.Count != 0)
                throw new InvalidOperationException("Periodic replacements cannot be rolled back this way.");
            var transaction = new AtomicTransaction();
            ImmutableDictionary<Guid, PeriodicEffectHandle> current = transaction.Read(registry.active);
            foreach (PreparedEffect effect in Added)
            {
                if (!current.TryGetValue(effect.Handle.Id, out PeriodicEffectHandle? existing) ||
                    !ReferenceEquals(existing, effect.Handle))
                    throw new InvalidOperationException("A reserved periodic effect is no longer current.");
                current = current.Remove(effect.Handle.Id);
            }
            if (Added.Count > 0)
                transaction.Stage(registry.active, current);
            transaction.Commit();
            IReadOnlyList<Exception> failures = Close(Added.Select(effect => effect.Handle));
            if (failures.Count > 0)
                throw new AggregateException("Periodic reservations were removed but failed to close.", failures);
        }

        private static IReadOnlyList<Exception> Close(IEnumerable<PeriodicEffectHandle> handles)
        {
            var failures = new List<Exception>();
            foreach (PeriodicEffectHandle handle in handles)
            {
                try { handle.Close(cancel: true); }
                catch (Exception error) { failures.Add(error); }
            }
            return failures;
        }
    }

    private readonly Context context;
    private readonly TriggerChainFactory factory;
    private readonly AtomicCell<ImmutableDictionary<Guid, PeriodicEffectHandle>> active =
        new(ImmutableDictionary<Guid, PeriodicEffectHandle>.Empty);
    private int publishedActiveCount;

    public PeriodicEffectRegistry(Context context, TriggerChainFactory factory)
    {
        this.context = context;
        this.factory = factory;
        context.AddObserver(this);
    }

    public int ActiveCount => Volatile.Read(ref publishedActiveCount);

    public PeriodicEffectHandle? TryReserve(
        AbilityTriggerChain chain, Unit? source, Unit target, DamageProfile damage,
        int tickCount, TimeSpan interval, TimeSpan firstTickDelay, TimeSpan? expiresAt,
        EventCancellation cancellation, PeriodicEffectHandle? replacement,
        out PeriodicEffectRejectionReason rejection, out PeriodicEffectHandle? retired)
    {
        var application = new PeriodicDamageApplication
        {
            Damage = damage, TickCount = tickCount, TickInterval = interval,
            FirstTickDelay = firstTickDelay, ExpiresAt = expiresAt, Replaces = replacement
        };
        var transaction = new AtomicTransaction();
        PreparedChanges? prepared = PrepareChanges(transaction, chain, source, target,
            [application], ImmutableHashSet<Guid>.Empty, null, ImmutableHashSet<Guid>.Empty,
            null, cancellation, out rejection, out _);
        retired = null;
        if (prepared == null) return null;
        try { transaction.Commit(); }
        catch (Exception error)
        {
            try { prepared.Abort(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Periodic reservation and cleanup failed.", error, cleanupError);
            }
            throw;
        }
        prepared.Publish();
        retired = prepared.Retired.SingleOrDefault();
        return prepared.Added.Single().Handle;
    }

    public PreparedChanges? TryReserveBatch(
        AbilityTriggerChain chain, Unit source, IReadOnlyList<Unit> targets,
        PeriodicDamageApplication application, EventCancellation cancellation,
        out PeriodicEffectRejectionReason rejection)
    {
        var transaction = new AtomicTransaction();
        PreparedChanges? prepared = PrepareBatch(transaction, chain, source, targets, application,
            cancellation, out rejection);
        if (prepared == null) return null;
        try { transaction.Commit(); }
        catch (Exception error)
        {
            try { prepared.Abort(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Periodic batch reservation and cleanup failed.", error, cleanupError);
            }
            throw;
        }
        return prepared;
    }

    public PreparedChanges? PrepareBatch(
        AtomicTransaction transaction, AbilityTriggerChain chain, Unit source, IReadOnlyList<Unit> targets,
        PeriodicDamageApplication application, EventCancellation cancellation,
        out PeriodicEffectRejectionReason rejection)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(application);
        if (targets.Count == 0 || targets.Distinct(ReferenceEqualityComparer.Instance).Count() != targets.Count)
            throw new InvalidOperationException("A periodic batch requires distinct targets.");
        if (application.Replaces != null)
            throw new InvalidOperationException("A periodic batch cannot replace an existing effect.");

        TargetedEffect[] additions = targets.Select(target => new TargetedEffect(
            target, application, null, null, ImmutableHashSet<Guid>.Empty)).ToArray();
        return PrepareChanges(transaction, chain, source, additions,
            ImmutableHashSet<Guid>.Empty, cancellation,
            out rejection, out _);
    }

    public PreparedChanges? PrepareStatusBatch(
        AtomicTransaction transaction, AbilityTriggerChain chain, Unit? source,
        IReadOnlyList<StatusChange> changes, EventCancellation cancellation,
        out PeriodicEffectRejectionReason rejection, out string? rejectedAbilityId)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(changes);
        if (!context.IsProcessingEvent || !ReferenceEquals(chain.Context, context))
            throw new InvalidOperationException("Periodic changes must be prepared by their owning context.");
        if (changes.Any(change => change == null || change.Target == null || change.Additions == null ||
            change.Origin == null || change.StatusApplicationId == Guid.Empty ||
            change.ReplacedStatusApplicationId == Guid.Empty))
            throw new InvalidOperationException("Each periodic status change requires a valid target and application.");
        if (changes.Select(change => change.Target).Distinct(ReferenceEqualityComparer.Instance).Count() != changes.Count ||
            changes.Select(change => change.StatusApplicationId).Distinct().Count() != changes.Count ||
            changes.Where(change => change.ReplacedStatusApplicationId.HasValue)
                .Select(change => change.ReplacedStatusApplicationId).Distinct().Count() !=
            changes.Count(change => change.ReplacedStatusApplicationId.HasValue))
            throw new InvalidOperationException("Periodic status changes must have distinct targets and application identities.");
        if (changes.Any(change => !context.ContainsReceiver(change.Target.EventReceiver) ||
            !ReferenceEquals(change.Target.EventReceiver.Context, context)))
            throw new InvalidOperationException("Periodic effect participants must belong to this context.");
        Prune();
        ImmutableDictionary<Guid, PeriodicEffectHandle> existing = transaction.Read(active);
        foreach (StatusChange change in changes)
        {
            if (existing.Values.Any(handle =>
                (handle.StatusApplicationId == change.StatusApplicationId ||
                 handle.StatusApplicationId == change.ReplacedStatusApplicationId &&
                    change.ReplacedStatusApplicationId.HasValue) &&
                !ReferenceEquals(handle.Target, change.Target)))
                throw new InvalidOperationException("A status application cannot replace effects on another target.");
        }
        var removed = changes.Where(change => change.ReplacedStatusApplicationId.HasValue)
            .Select(change => change.ReplacedStatusApplicationId!.Value).ToHashSet();
        TargetedEffect[] additions = changes.SelectMany(change =>
        {
            IReadOnlySet<Guid> replaceable = ImmutableHashSet.Create(
                change.ReplacedStatusApplicationId ?? change.StatusApplicationId);
            return change.Additions.Select(application => new TargetedEffect(
                change.Target, application, change.StatusApplicationId, change.Origin, replaceable));
        }).ToArray();
        return PrepareChanges(transaction, chain, source, additions, removed, cancellation,
            out rejection, out rejectedAbilityId);
    }

    public PreparedChanges? PrepareChanges(
        AtomicTransaction transaction, AbilityTriggerChain chain, Unit? source, Unit target,
        IReadOnlyList<PeriodicEffectApplication> additions, IReadOnlySet<Guid> removedStatusApplications,
        Guid? statusApplicationId, IReadOnlySet<Guid> replacementStatusApplications,
        StatusApplicationOrigin? applicationOrigin, EventCancellation cancellation,
        out PeriodicEffectRejectionReason rejection, out string? rejectedAbilityId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(replacementStatusApplications);
        if (statusApplicationId == Guid.Empty || replacementStatusApplications.Contains(Guid.Empty))
            throw new ArgumentOutOfRangeException(nameof(statusApplicationId));
        return PrepareChanges(transaction, chain, source,
            additions.Select(application => new TargetedEffect(target, application, statusApplicationId,
                applicationOrigin, replacementStatusApplications)).ToArray(),
            removedStatusApplications, cancellation, out rejection, out rejectedAbilityId);
    }

    private PreparedChanges? PrepareChanges(
        AtomicTransaction transaction, AbilityTriggerChain chain, Unit? source,
        IReadOnlyList<TargetedEffect> additions, IReadOnlySet<Guid> removedStatusApplications,
        EventCancellation cancellation,
        out PeriodicEffectRejectionReason rejection, out string? rejectedAbilityId)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(removedStatusApplications);
        if (!context.IsProcessingEvent)
            throw new InvalidOperationException("Periodic effects must be reserved by the context's execution owner.");
        if (!ReferenceEquals(chain.Context, context))
            throw new InvalidOperationException("The periodic effect belongs to another context.");
        if (removedStatusApplications.Contains(Guid.Empty))
            throw new ArgumentOutOfRangeException(nameof(removedStatusApplications));

        if (additions.Count > 0 && source != null &&
            (!context.ContainsReceiver(source.EventReceiver) ||
             !ReferenceEquals(source.EventReceiver.Context, context)))
            throw new InvalidOperationException("Periodic effect participants must belong to this context.");

        foreach (TargetedEffect addition in additions)
        {
            Unit target = addition.Target;
            PeriodicEffectApplication application = addition.Application;
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(application);
            ArgumentNullException.ThrowIfNull(addition.ReplacementStatusApplications);
            if (addition.StatusApplicationId == Guid.Empty ||
                addition.ReplacementStatusApplications.Contains(Guid.Empty))
                throw new ArgumentOutOfRangeException(nameof(addition.StatusApplicationId));
            if (addition.StatusApplicationId.HasValue != (addition.Origin != null))
                throw new InvalidOperationException("Status periodic effects require their application identity and origin.");
            if (application.StackOwner != null && !addition.StatusApplicationId.HasValue)
                throw new InvalidOperationException("A stacked periodic effect requires its status identity.");
            if (!context.ContainsReceiver(target.EventReceiver) ||
                !ReferenceEquals(target.EventReceiver.Context, context))
                throw new InvalidOperationException("Periodic effect participants must belong to this context.");
            if (target.EventReceiver.GetHandlers<AbilityLifecycleHandler>().Count != 1)
                throw new InvalidOperationException("The target requires exactly one periodic lifecycle handler.");
            switch (application)
            {
                case PeriodicDamageApplication damage:
                    ArgumentNullException.ThrowIfNull(damage.Damage);
                    IReadOnlyList<BeingDamageAbility> receivers = target.EventReceiver.GetHandlers<BeingDamageAbility>();
                    if (receivers.Count != 1 || !ReferenceEquals(receivers[0].DamageTypes, damage.Damage.DamageTypes))
                        throw new InvalidOperationException("Periodic damage requires one receiver with the matching damage catalog.");
                    RequireSettlement(target, TriggerType.ScheduledDamageTick,
                        target.EventReceiver.GetHandlers<ScheduledDamageHandler>());
                    break;
                case PeriodicHealApplication heal:
                    ArgumentNullException.ThrowIfNull(heal.Heal);
                    if (target.EventReceiver.GetHandlers<HealingHandler>().Count != 1)
                        throw new InvalidOperationException("Periodic healing requires exactly one healing handler.");
                    RequireSettlement(target, TriggerType.ScheduledHealTick,
                        target.EventReceiver.GetHandlers<ScheduledHealHandler>());
                    break;
                default:
                    throw new InvalidOperationException("The periodic effect type is not supported.");
            }
            if (string.IsNullOrWhiteSpace(application.EffectId))
                throw new InvalidOperationException("A periodic effect requires an effect ID.");
            if (application.TickCount <= 0 ||
                application.TickCount > 1 && application.TickInterval <= TimeSpan.Zero)
                throw new InvalidOperationException("Periodic effects require ticks and a positive repeat interval.");
            if (application.TickInterval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(application.TickInterval));
            if (application.FirstTickDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(application.FirstTickDelay));
            if (application.ExpiresAt is { } expiry && expiry <= context.ContextTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(application.ExpiresAt),
                    "Expiry must be after the current simulation time.");
        }

        Prune();
        ImmutableDictionary<Guid, PeriodicEffectHandle> current = transaction.Read(active);
        var retired = current.Values
            .Where(handle => handle.StatusApplicationId is { } id && removedStatusApplications.Contains(id))
            .ToDictionary(handle => handle.Id);
        var explicitReplacements = new HashSet<Guid>();
        foreach (TargetedEffect addition in additions)
        {
            Unit target = addition.Target;
            PeriodicEffectApplication application = addition.Application;
            PeriodicEffectHandle? replacement = application.Replaces;
            if (replacement == null) continue;
            if (!explicitReplacements.Add(replacement.Id) ||
                !ReferenceEquals(replacement.Registry, this) || !ReferenceEquals(replacement.Target, target) ||
                !current.TryGetValue(replacement.Id, out PeriodicEffectHandle? existing) ||
                !ReferenceEquals(existing, replacement) || replacement.Cancellation.IsCancellationRequested)
                throw new InvalidOperationException("The replaced periodic effect is not active on this target and context.");
            if (addition.StatusApplicationId.HasValue
                ? replacement.StatusApplicationId is not { } owner ||
                    !addition.ReplacementStatusApplications.Contains(owner)
                : replacement.StatusApplicationId.HasValue)
                throw new InvalidOperationException("The periodic replacement belongs to another status application.");
            retired[replacement.Id] = replacement;
        }

        var chains = new List<AbilityTriggerChain>(additions.Count);
        foreach (TargetedEffect addition in additions)
        {
            PeriodicEffectApplication application = addition.Application;
            AbilityTriggerChain? periodicChain = chain.EnterPeriodic(application.EffectId);
            if (periodicChain == null)
            {
                rejection = PeriodicEffectRejectionReason.NestingDepth;
                rejectedAbilityId = application.EffectId;
                return null;
            }
            chains.Add(periodicChain);
        }
        if ((long)current.Count - retired.Count + additions.Count > factory.MaxActivePeriodicEffects)
        {
            string abilityId = additions[0].Application.EffectId;
            factory.Reject(new TriggerLimitDiagnostic(
                TriggerLimitReason.ActivePeriodicEffects, abilityId, chain.PeriodicDepth,
                chain.TriggerCount, factory.MaxActivePeriodicEffects)
            { ActivePeriodicEffects = current.Count });
            rejection = PeriodicEffectRejectionReason.Capacity;
            rejectedAbilityId = abilityId;
            return null;
        }
        if (cancellation.IsCancellationRequested)
        {
            rejection = PeriodicEffectRejectionReason.Canceled;
            rejectedAbilityId = null;
            return null;
        }

        var added = new List<PreparedEffect>(additions.Count);
        try
        {
            for (int index = 0; index < additions.Count; index++)
            {
                Unit target = additions[index].Target;
                PeriodicEffectApplication application = additions[index].Application;
                var handle = new PeriodicEffectHandle(this, target, chains[index], cancellation,
                    application.ExpiresAt, additions[index].StatusApplicationId, additions[index].Origin,
                    application.StackOwner);
                added.Add(new PreparedEffect(application, handle));
                if (handle.Cancellation.IsCancellationRequested)
                {
                    new PreparedChanges(this, added, []).Abort();
                    rejection = PeriodicEffectRejectionReason.Canceled;
                    rejectedAbilityId = null;
                    return null;
                }
            }

            if (retired.Count > 0 || added.Count > 0)
                transaction.Update(active, staged =>
                {
                    ImmutableDictionary<Guid, PeriodicEffectHandle> next = staged.RemoveRange(retired.Keys);
                    foreach (PreparedEffect effect in added) next = next.Add(effect.Handle.Id, effect.Handle);
                    return next;
                });
        }
        catch (Exception error)
        {
            try { new PreparedChanges(this, added, []).Abort(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Periodic preparation and cleanup failed.", error, cleanupError);
            }
            throw;
        }
        rejection = PeriodicEffectRejectionReason.None;
        rejectedAbilityId = null;
        return new PreparedChanges(this, added, retired.Values.ToArray());
    }

    private static void RequireSettlement<T>(Unit target, TriggerType type, IReadOnlyList<T> handlers)
        where T : IRuntimeInstanceHandler
    {
        if (handlers.Count != 1)
            throw new InvalidOperationException("A periodic effect requires exactly one settlement handler.");
        int routers = target.EventReceiver.GetHandlers<TriggerHandler>().Count(trigger =>
            trigger.IsPeriodicSettlement && trigger.TriggerType == type &&
            trigger.TargetInstanceId == handlers[0].RuntimeInstanceId);
        if (routers != 1)
            throw new InvalidOperationException("The periodic settlement handler requires exactly one bound router.");
    }

    public static IEnumerable<Event> CreateStartEvents(
        Context context, Guid commandId, Unit? source, Unit target, EventReceiver? completionReceiver,
        PeriodicEffectApplication application, PeriodicEffectHandle effect, Guid operationId)
    {
        yield return context.Factory.NewEvent<PeriodicEffectStartedEvent>() with
        {
            CommandEventId = commandId, Target = target, Effect = effect,
            Receiver = completionReceiver ?? target.EventReceiver, IsScopeDetached = true,
            CommittedOperationId = operationId
        };
        yield return context.Factory.NewEvent<AbilityLifecycleEvent>() with
        {
            CommandEventId = commandId, Source = source, Target = target,
            Stage = AbilityLifecycleStage.Start, RemainingTicks = application.TickCount,
            TickInterval = application.TickInterval,
            TickDamage = (application as PeriodicDamageApplication)?.Damage,
            TickHeal = (application as PeriodicHealApplication)?.Heal, PeriodicEffect = effect,
            TriggerChain = effect.Chain, Receiver = target.EventReceiver,
            TimeSpan = application.FirstTickDelay, CancellationTokenSource = effect.Cancellation,
            IsScopeDetached = true
        };
        if (application.ExpiresAt is { } expiresAt)
            yield return context.Factory.NewEvent<PeriodicEffectExpiredEvent>() with
            {
                Effect = effect, Receiver = target.EventReceiver,
                TimeSpan = expiresAt - context.ContextTimeSpan, IsScopeDetached = true
            };
    }

    internal void NotifyCanceled(PeriodicEffectHandle handle)
    {
        bool posted = context.Inbox.TryPost(new PeriodicEffectCanceledEvent
        {
            Effect = handle, Receiver = handle.Target.EventReceiver, IsScopeDetached = true
        });
        if (!posted && !context.IsDisposed)
            throw new InvalidOperationException("The periodic cancellation could not be posted to the context.");
    }

    public bool IsCurrent(PeriodicEffectHandle handle) =>
        ReferenceEquals(handle.Registry, this) &&
        active.Value.TryGetValue(handle.Id, out PeriodicEffectHandle? current) &&
        ReferenceEquals(current, handle) && !handle.Cancellation.IsCancellationRequested;

    public void Finish(PeriodicEffectHandle handle, bool cancel)
    {
        var transaction = new AtomicTransaction();
        ImmutableDictionary<Guid, PeriodicEffectHandle> current = transaction.Read(active);
        if (!ReferenceEquals(handle.Registry, this) ||
            !current.TryGetValue(handle.Id, out PeriodicEffectHandle? entry) ||
            !ReferenceEquals(entry, handle))
            return;
        transaction.Stage(active, current.Remove(handle.Id));
        transaction.Commit();
        Volatile.Write(ref publishedActiveCount, active.Value.Count);
        handle.Close(cancel);
    }

    private void Prune()
    {
        foreach (PeriodicEffectHandle handle in active.Value.Values.Where(handle =>
            handle.Cancellation.IsCancellationRequested ||
            !context.ContainsReceiver(handle.Target.EventReceiver) ||
            handle.Target.IsRetired ||
            !handle.Target.IsAlive && handle.Target.LifecycleOnDeath == AbilityLifecycleDeathPolicy.Stop ||
            handle.ExpiresAt is { } expiry && expiry <= context.ContextTimeSpan).ToArray())
            Finish(handle, cancel: true);
    }

    public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];

    public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev)
    {
        if (ev is PeriodicEffectExpiredEvent expired)
            Finish(expired.Effect, cancel: true);
        else if (ev is PeriodicEffectCanceledEvent canceled)
            Finish(canceled.Effect, cancel: false);
        else if (ev is AbilityLifecycleEvent { PeriodicEffect: { } effect } &&
            ev.CancellationToken.IsCancellationRequested)
            Finish(effect, cancel: true);
        else if (ev is ScheduledDamageTickEvent { PeriodicEffect: { } tickEffect } &&
            ev.CancellationToken.IsCancellationRequested)
            Finish(tickEffect, cancel: true);
        else if (ev is ScheduledHealTickEvent { PeriodicEffect: { } healEffect } &&
            ev.CancellationToken.IsCancellationRequested)
            Finish(healEffect, cancel: true);
        else if (ev is UnitRetiredEvent or DeathResolvedEvent)
            Prune();
        return [];
    }

    public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation)
    {
        if (!handlerInvocation && ev is ScheduledDamageTickEvent
            { IsFinalTick: true, PeriodicEffect: { } effect })
            Finish(effect, cancel: false);
        else if (!handlerInvocation && ev is ScheduledHealTickEvent
            { IsFinalTick: true, PeriodicEffect: { } healEffect })
            Finish(healEffect, cancel: false);
        return [];
    }

    public void OnPendingEventsCleared(Context context)
    {
        List<Exception>? failures = null;
        foreach (PeriodicEffectHandle handle in active.Value.Values.ToArray())
        {
            try { Finish(handle, cancel: true); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is { Count: > 0 })
            throw new AggregateException("One or more periodic effects failed to close.", failures);
    }
}
