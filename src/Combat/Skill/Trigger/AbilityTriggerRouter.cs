using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill;

internal sealed record TriggerRoutingChange(
    Unit Holder, IReadOnlyList<IHandler> Remove, IReadOnlyList<IHandler> Add);

internal sealed class AbilityTriggerRouter : IContextObserver
{
    private sealed record Registration(Unit Holder, TriggerHandler Trigger, long Order);

    private sealed class Index
    {
        public Index(IReadOnlyList<Registration> registrations, long nextOrder)
        {
            Registrations = registrations;
            NextOrder = nextOrder;
            var keyed = new Dictionary<(TriggerType Type, string AbilityId), List<Registration>>();
            var any = new Dictionary<TriggerType, List<Registration>>();
            foreach (Registration registration in registrations)
            {
                AbilityTriggerConfig config = registration.Trigger.Config;
                IReadOnlyList<string>? ids = config.SourceAbilityIds;
                if ((ids == null || ids.Count == 0) && !string.IsNullOrEmpty(config.SourceAbilityId))
                    ids = [config.SourceAbilityId];
                if ((ids == null || ids.Count == 0) && config.Type == TriggerType.Command)
                    ids = [registration.Trigger.TargetAbilityId];
                if (config.Type == TriggerType.Signal)
                    ids = [config.SignalId!];
                if (ids == null || ids.Count == 0)
                {
                    if (!any.TryGetValue(config.Type, out List<Registration>? entries))
                        any.Add(config.Type, entries = []);
                    entries.Add(registration);
                    continue;
                }
                foreach (string id in ids)
                {
                    var key = (config.Type, id);
                    if (!keyed.TryGetValue(key, out List<Registration>? entries))
                        keyed.Add(key, entries = []);
                    entries.Add(registration);
                }
            }
            Keyed = keyed;
            Any = any;
        }

        public IReadOnlyList<Registration> Registrations { get; }
        public long NextOrder { get; }
        public Dictionary<(TriggerType Type, string AbilityId), List<Registration>> Keyed { get; }
        public Dictionary<TriggerType, List<Registration>> Any { get; }
    }

    private readonly Context context;
    private readonly AtomicCell<Index> index = new(new Index([], 0));

    public AbilityTriggerRouter(Context context)
    {
        this.context = context;
        context.RegisterIndexedEvent<ProjectileHitEvent>();
        context.RegisterIndexedEvent<CommandEvent>();
        context.RegisterIndexedEvent<ProjectileExplosionEvent>();
        context.RegisterIndexedEvent<DamageLifeReduceEvent>();
        context.RegisterIndexedEvent<DamageEvent>();
        context.RegisterIndexedEvent<DeathResolvedEvent>();
        context.RegisterIndexedEvent<DeathEvent>();
        context.RegisterIndexedEvent<HealEvent>();
        context.RegisterIndexedEvent<AbilitySignalEvent>();
        context.RegisterIndexedEvent<UnitLevelUpEvent>();
        context.RegisterIndexedEvent<ScheduledDamageTickEvent>();
        context.RegisterIndexedEvent<ScheduledHealTickEvent>();
        context.AddObserver(this);
    }

    public void StageChanges(AtomicTransaction transaction, IReadOnlyList<TriggerRoutingChange> changes)
    {
        Index current = transaction.Read(index);
        var next = current.Registrations.ToList();
        long nextOrder = current.NextOrder;
        bool changed = false;
        foreach (TriggerRoutingChange change in changes)
        {
            foreach (TriggerHandler trigger in change.Remove.OfType<TriggerHandler>()
                .Where(handler => IsIndexed(handler.TriggerType)))
            {
                int position = next.FindIndex(entry =>
                    ReferenceEquals(entry.Holder, change.Holder) && ReferenceEquals(entry.Trigger, trigger));
                if (position < 0)
                    throw new InvalidOperationException("The removed trigger is not registered in the routing index.");
                next.RemoveAt(position);
                changed = true;
            }
        }
        foreach (TriggerRoutingChange change in changes)
        {
            foreach (TriggerHandler trigger in change.Add.OfType<TriggerHandler>()
                .Where(handler => IsIndexed(handler.TriggerType)))
            {
                if (next.Any(entry => ReferenceEquals(entry.Trigger, trigger)))
                    throw new InvalidOperationException("A trigger cannot be registered on multiple holders.");
                if (string.IsNullOrWhiteSpace(trigger.TargetAbilityId))
                    throw new InvalidOperationException("A trigger requires a target ability ID.");
                if (trigger.TriggerType == TriggerType.Signal && string.IsNullOrWhiteSpace(trigger.Config.SignalId))
                    throw new InvalidOperationException("A signal trigger requires a signal ID.");
                IReadOnlyList<string>? ids = trigger.Config.SourceAbilityIds;
                if (ids != null && (ids.Any(string.IsNullOrWhiteSpace) ||
                    ids.Distinct(StringComparer.Ordinal).Count() != ids.Count))
                    throw new InvalidOperationException("A trigger has invalid or duplicate source ability IDs.");
                trigger.Config.StageFreeze(transaction);
                next.Add(new Registration(change.Holder, trigger, checked(nextOrder++)));
                changed = true;
            }
        }
        if (changed) transaction.Stage(index, new Index(next.ToArray(), nextOrder));
    }

    public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];
    public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev) => [];

    private static bool IsIndexed(TriggerType type) =>
        type is TriggerType.Command or TriggerType.ProjectileHit or TriggerType.ProjectileExplosion or
            TriggerType.TargetLifeReduce or TriggerType.Death or TriggerType.Heal or TriggerType.Signal or
            TriggerType.ScheduledDamageTick or TriggerType.ScheduledHealTick or
            TriggerType.DamageSkillHit or TriggerType.BeingDamaged or TriggerType.UnitLevelUp;

    private IEnumerable<Registration> Candidates(TriggerType type, string? abilityId)
    {
        Index snapshot = index.Value;
        IEnumerable<Registration> candidates = snapshot.Any.TryGetValue(type, out List<Registration>? any)
            ? any : [];
        if (abilityId != null && snapshot.Keyed.TryGetValue((type, abilityId), out List<Registration>? keyed))
            candidates = candidates.Concat(keyed);
        return candidates.OrderBy(entry => entry.Order);
    }

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanCommand(CommandEvent command)
    {
        if (!ReferenceEquals(command.Context, context))
            throw new InvalidOperationException("The command belongs to a different context.");
        return PlanSingle(TriggerType.Command, command.AbilityId, command.Source.EventReceiver,
            holder => context.Factory.NewEvent<CommandTriggerObservationEvent>() with
            {
                Command = command, Holder = holder, Receiver = holder.EventReceiver,
                Priority = command.Priority, Causality = command.Causality,
                Scope = command.Scope, CancellationTokenSource = command.CancellationTokenSource,
                CorrelationId = command.CorrelationId, CommittedOperationId = command.CommittedOperationId
            });
    }

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanScheduledDamage(ScheduledDamageTickEvent tick) =>
        PlanPeriodic(tick, tick.Target, TriggerType.ScheduledDamageTick, tick.Damage.Ability?.DefinitionId,
            holder => context.Factory.NewEvent<ScheduledDamageTriggerObservationEvent>() with
            {
                Tick = tick, Holder = holder, Receiver = holder.EventReceiver,
                Priority = tick.Priority, Causality = tick.Causality,
                Scope = tick.Scope, CancellationTokenSource = tick.CancellationTokenSource,
                CorrelationId = tick.CorrelationId, CommittedOperationId = tick.CommittedOperationId
            });

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanScheduledHeal(ScheduledHealTickEvent tick) =>
        PlanPeriodic(tick, tick.Target, TriggerType.ScheduledHealTick, tick.Heal.Ability?.DefinitionId,
            holder => context.Factory.NewEvent<ScheduledHealTriggerObservationEvent>() with
            {
                Tick = tick, Holder = holder, Receiver = holder.EventReceiver,
                Priority = tick.Priority, Causality = tick.Causality,
                Scope = tick.Scope, CancellationTokenSource = tick.CancellationTokenSource,
                CorrelationId = tick.CorrelationId, CommittedOperationId = tick.CommittedOperationId
            });

    private (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanPeriodic(Event tick, Unit target, TriggerType type, string? abilityId, Func<Unit, Event> createObservation)
    {
        if (!ReferenceEquals(tick.Context, context))
            throw new InvalidOperationException("The scheduled tick belongs to a different context.");
        EventReceiver receiver = target.EventReceiver;
        TriggerHandler settlement = receiver.GetHandlers<TriggerHandler>()
            .SingleOrDefault(trigger => trigger.IsPeriodicSettlement && trigger.TriggerType == type) ??
            throw new InvalidOperationException("The scheduled tick requires its periodic settlement router.");
        var (local, other) = PlanSingle(type, abilityId, receiver, createObservation);
        return (local.Concat(receiver.PlanIndexedHandler(tick, settlement)).ToArray(), other);
    }

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanUnitLevelUp(UnitLevelUpEvent upgrade)
    {
        if (!ReferenceEquals(upgrade.Context, context))
            throw new InvalidOperationException("The level increase belongs to a different context.");
        return PlanSingle(TriggerType.UnitLevelUp, null, upgrade.Unit.EventReceiver,
            holder => context.Factory.NewEvent<UnitLevelUpTriggerObservationEvent>() with
            {
                Upgrade = upgrade, Holder = holder, Receiver = holder.EventReceiver,
                Priority = upgrade.Priority, Causality = upgrade.Causality,
                Scope = upgrade.Scope, CancellationTokenSource = upgrade.CancellationTokenSource,
                CorrelationId = upgrade.CorrelationId, CommittedOperationId = upgrade.CommittedOperationId
            });
    }

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanSignal(AbilitySignalEvent signal)
    {
        if (!ReferenceEquals(signal.Context, context))
            throw new InvalidOperationException("The signal belongs to a different context.");
        return PlanSingle(TriggerType.Signal, signal.SignalId, (signal.Source ?? signal.Target).EventReceiver,
            holder => context.Factory.NewEvent<SignalTriggerObservationEvent>() with
            {
                Signal = signal, Holder = holder, Receiver = holder.EventReceiver,
                Priority = signal.Priority, Causality = signal.Causality,
                Scope = signal.Scope, CancellationTokenSource = signal.CancellationTokenSource,
                CorrelationId = signal.CorrelationId, CommittedOperationId = signal.CommittedOperationId
            });
    }

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanExplosion(ProjectileExplosionEvent explosion)
    {
        if (!ReferenceEquals(explosion.Context, context))
            throw new InvalidOperationException("The explosion belongs to a different context.");
        return PlanSingle(TriggerType.ProjectileExplosion, explosion.AbilityId, explosion.Source.EventReceiver,
            holder => context.Factory.NewEvent<ProjectileExplosionTriggerObservationEvent>() with
            {
                Explosion = explosion, Holder = holder, Receiver = holder.EventReceiver,
                Priority = explosion.Priority, Causality = explosion.Causality,
                Scope = explosion.Scope, CancellationTokenSource = explosion.CancellationTokenSource,
                CorrelationId = explosion.CorrelationId, CommittedOperationId = explosion.CommittedOperationId
            });
    }

    public (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanConfirmedDeath(DeathResolvedEvent death)
    {
        if (!ReferenceEquals(death.Context, context))
            throw new InvalidOperationException("The confirmed death belongs to a different context.");
        return PlanSingle(TriggerType.Death, death.DamageAbility?.Ability?.DefinitionId, death.Victim.EventReceiver,
            holder => context.Factory.NewEvent<ConfirmedDeathTriggerObservationEvent>() with
            {
                Death = death, Holder = holder, Receiver = holder.EventReceiver,
                Priority = death.Priority, Causality = death.Causality,
                Scope = death.Scope, CancellationTokenSource = death.CancellationTokenSource,
                CorrelationId = death.CorrelationId, CommittedOperationId = death.CommittedOperationId
            });
    }

    private (IReadOnlyList<IndexedHandlerInvocation> Local, IReadOnlyList<IndexedHandlerInvocation> Other)
        PlanSingle(TriggerType type, string? abilityId, EventReceiver receiver, Func<Unit, Event> createObservation)
    {
        var local = new List<IndexedHandlerInvocation>();
        var other = new List<IndexedHandlerInvocation>();
        foreach (Registration registration in Candidates(type, abilityId))
        {
            if (!context.ContainsReceiver(registration.Holder.EventReceiver))
                continue;
            Event observation = createObservation(registration.Holder);
            List<IndexedHandlerInvocation> destination =
                ReferenceEquals(registration.Holder.EventReceiver, receiver) ? local : other;
            destination.AddRange(registration.Holder.EventReceiver.PlanIndexedHandler(observation, registration.Trigger));
        }
        return (local, other);
    }

    public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation)
    {
        if (handlerInvocation || ev is not
            (ProjectileHitPublicationEvent or DamageLifeReducePublicationEvent or PendingDeathPublicationEvent or
                HealPublicationEvent or DamagePublicationEvent))
            yield break;
        if (!ReferenceEquals(context, this.context))
            throw new InvalidOperationException("The trigger router belongs to a different context.");

        if (ev is DamagePublicationEvent damagePublication)
        {
            DamageEvent damage = damagePublication.Damage;
            if (!damage.Publication.Dispatched) yield break;
            IEnumerable<Registration> registrations =
                Candidates(TriggerType.DamageSkillHit, damage.DamageAbilityId)
                    .Concat(Candidates(TriggerType.BeingDamaged, damage.DamageAbilityId))
                    .OrderBy(entry => entry.Order);
            foreach (Registration registration in registrations)
            {
                if (!context.ContainsReceiver(registration.Holder.EventReceiver))
                    continue;
                var observation = context.Factory.NewEvent<DamageTriggerObservationEvent>() with
                {
                    Damage = damage, Holder = registration.Holder, Receiver = registration.Holder.EventReceiver,
                    Priority = damagePublication.Priority, Causality = damagePublication.Causality,
                    Scope = damagePublication.Replay?.Scope ?? damage.PublicationScope,
                    CancellationTokenSource = damage.CancellationTokenSource,
                    CorrelationId = damage.CorrelationId, CommittedOperationId = damage.CommittedOperationId
                };
                damage.Publication.InvocationsFor(registration.Holder.EventReceiver)
                    .AddRange(registration.Holder.EventReceiver.PlanIndexedHandler(observation, registration.Trigger));
            }
        }
        else if (ev is ProjectileHitPublicationEvent publication)
        {
            ProjectileHitEvent hit = publication.Hit;
            foreach (Registration registration in Candidates(TriggerType.ProjectileHit, hit.AbilityId))
            {
                if (!context.ContainsReceiver(registration.Holder.EventReceiver))
                    continue;
                var observation = context.Factory.NewEvent<ProjectileHitTriggerObservationEvent>() with
                {
                    Hit = hit, Holder = registration.Holder, Receiver = registration.Holder.EventReceiver,
                    Priority = publication.Priority, Causality = publication.Causality,
                    Scope = publication.Scope, CancellationTokenSource = publication.CancellationTokenSource,
                    CorrelationId = publication.CorrelationId, CommittedOperationId = publication.CommittedOperationId
                };
                hit.Publication.InvocationsFor(registration.Holder.EventReceiver)
                    .AddRange(registration.Holder.EventReceiver.PlanIndexedHandler(observation, registration.Trigger));
            }
        }
        else if (ev is DamageLifeReducePublicationEvent lifePublication)
        {
            DamageLifeReduceEvent reduction = lifePublication.Reduction;
            foreach (Registration registration in Candidates(TriggerType.TargetLifeReduce, reduction.DamageAbility.Ability?.DefinitionId))
            {
                if (!context.ContainsReceiver(registration.Holder.EventReceiver))
                    continue;
                var observation = context.Factory.NewEvent<DamageLifeReduceTriggerObservationEvent>() with
                {
                    Reduction = reduction, Holder = registration.Holder, Receiver = registration.Holder.EventReceiver,
                    Priority = lifePublication.Priority, Causality = lifePublication.Causality,
                    Scope = lifePublication.Scope, CancellationTokenSource = lifePublication.CancellationTokenSource,
                    CorrelationId = lifePublication.CorrelationId, CommittedOperationId = lifePublication.CommittedOperationId
                };
                reduction.Publication.InvocationsFor(registration.Holder.EventReceiver)
                    .AddRange(registration.Holder.EventReceiver.PlanIndexedHandler(observation, registration.Trigger));
            }
        }
        else if (ev is PendingDeathPublicationEvent deathPublication)
        {
            DeathEvent attempt = deathPublication.Attempt;
            foreach (Registration registration in Candidates(TriggerType.Death, attempt.DamageAbility.Ability?.DefinitionId))
            {
                if (!context.ContainsReceiver(registration.Holder.EventReceiver))
                    continue;
                var observation = context.Factory.NewEvent<PendingDeathTriggerObservationEvent>() with
                {
                    Attempt = attempt, Holder = registration.Holder, Receiver = registration.Holder.EventReceiver,
                    Priority = deathPublication.Priority, Causality = deathPublication.Causality,
                    Scope = deathPublication.Scope, CancellationTokenSource = deathPublication.CancellationTokenSource,
                    CorrelationId = deathPublication.CorrelationId, CommittedOperationId = deathPublication.CommittedOperationId
                };
                attempt.Publication.InvocationsFor(registration.Holder.EventReceiver)
                    .AddRange(registration.Holder.EventReceiver.PlanIndexedHandler(observation, registration.Trigger));
            }
        }
        else if (ev is HealPublicationEvent healPublication)
        {
            HealEvent heal = healPublication.Heal;
            foreach (Registration registration in Candidates(TriggerType.Heal, heal.HealAbilityId))
            {
                if (!context.ContainsReceiver(registration.Holder.EventReceiver))
                    continue;
                var observation = context.Factory.NewEvent<HealTriggerObservationEvent>() with
                {
                    Heal = heal, Holder = registration.Holder, Receiver = registration.Holder.EventReceiver,
                    Priority = healPublication.Priority, Causality = healPublication.Causality,
                    Scope = healPublication.Scope, CancellationTokenSource = healPublication.CancellationTokenSource,
                    CorrelationId = healPublication.CorrelationId, CommittedOperationId = healPublication.CommittedOperationId
                };
                heal.Publication.InvocationsFor(registration.Holder.EventReceiver)
                    .AddRange(registration.Holder.EventReceiver.PlanIndexedHandler(observation, registration.Trigger));
            }
        }
    }
}
