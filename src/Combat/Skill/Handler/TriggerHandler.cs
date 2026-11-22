using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.Handler
{
    /// <summary>
    /// Trigger owner: evaluates trigger conditions and emits <see cref="AbilityTriggerInvokeEvent"/>.
    /// Pure trigger evaluation only - it does not instantiate or mount other handlers.
    /// </summary>
    public class TriggerHandler : Handler, IRequiresCurrentRegistration, IAbilityRuntimePart, IIndexedEventHandler, ITargetableHandler
    {
        public AbilitySourceBinding Binding { get; } = new();
        public bool RequiresCurrentRegistration(IEvent ev) => true;
        internal bool IsPeriodicSettlement { get; init; }
        private readonly AtomicCell<Guid?> targetInstanceId = new(null);
        public Guid? TargetInstanceId => targetInstanceId.Value;
        private int? chargeRestoreAmount;
        public int? ChargeRestoreAmount
        {
            get => chargeRestoreAmount;
            set
            {
                EnsureMutable();
                if (value is <= 0) throw new ArgumentOutOfRangeException(nameof(value));
                chargeRestoreAmount = value;
            }
        }

        public void StageBindTarget(AtomicTransaction transaction, Guid instanceId)
        {
            if (instanceId == Guid.Empty)
                throw new ArgumentOutOfRangeException(nameof(instanceId));
            Guid? current = transaction.Read(targetInstanceId);
            if (current is { } existing && existing != instanceId)
                throw new InvalidOperationException("The trigger is already bound to another ability instance.");
            if (current == null && Config.IsFrozen)
                throw new InvalidOperationException("An installed trigger cannot be rebound.");
            if (current == null)
                transaction.Stage(targetInstanceId, instanceId);
        }

        private AbilityTriggerConfig config = new();
        public AbilityTriggerConfig Config
        {
            get => config;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (config.IsFrozen)
                    throw new InvalidOperationException("An installed trigger configuration cannot be replaced.");
                config = value;
            }
        }

        public TriggerType TriggerType
        {
            get => Config.Type;
            set => Config.Type = value;
        }

        private string targetAbilityId = string.Empty;
        public required string TargetAbilityId
        {
            get => targetAbilityId;
            set { EnsureMutable(); targetAbilityId = value; }
        }

        public override string Id
        {
            get => base.Id;
            set { EnsureMutable(); base.Id = value; }
        }

        public override int Priority
        {
            get => base.Priority;
            set { EnsureMutable(); base.Priority = value; }
        }

        private void EnsureMutable()
        {
            if (Config.IsFrozen)
                throw new InvalidOperationException("An installed trigger registration cannot be changed.");
        }

        public string? SourceAbilityId
        {
            get => Config.SourceAbilityId;
            set => Config.SourceAbilityId = value;
        }

        public IReadOnlyList<string>? SourceAbilityIds
        {
            get => Config.SourceAbilityIds;
            set => Config.SourceAbilityIds = value;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnScheduledDamageTick(ScheduledDamageTickEvent ev)
        {
            if (!IsPeriodicSettlement || TriggerType != TriggerType.ScheduledDamageTick ||
                ev.Receiver != ev.Target.EventReceiver || ev.CancellationToken.IsCancellationRequested ||
                !CanTrigger(ev))
                yield break;

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId,
                ev.Source, ev.Target, ev.Target.MapObject?.Position,
                ev.Damage.Id);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnScheduledHealTick(ScheduledHealTickEvent ev)
        {
            if (!IsPeriodicSettlement || TriggerType != TriggerType.ScheduledHealTick ||
                ev.Receiver != ev.Target.EventReceiver || ev.CancellationToken.IsCancellationRequested ||
                !CanTrigger(ev))
                yield break;

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId,
                ev.Source, ev.Target, ev.Target.MapObject?.Position,
                ev.Heal.Id);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedScheduledDamage(ScheduledDamageTriggerObservationEvent observation)
        {
            ScheduledDamageTickEvent tick = observation.Tick;
            Unit holder = observation.Holder;
            if (IsPeriodicSettlement || TriggerType != TriggerType.ScheduledDamageTick ||
                !MatchesSource(tick.Damage.Ability?.DefinitionId) ||
                !Config.MatchesEventSource(tick.Source, holder) || !CanTrigger(tick, holder))
                yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(tick, tick.CommandEventId,
                holder, tick.Target, tick.Target.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedScheduledHeal(ScheduledHealTriggerObservationEvent observation)
        {
            ScheduledHealTickEvent tick = observation.Tick;
            Unit holder = observation.Holder;
            if (IsPeriodicSettlement || TriggerType != TriggerType.ScheduledHealTick ||
                !MatchesSource(tick.Heal.Ability?.DefinitionId) ||
                !Config.MatchesEventSource(tick.Source, holder) || !CanTrigger(tick, holder))
                yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(tick, tick.CommandEventId,
                holder, tick.Target, tick.Target.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCommand(CommandEvent ev) => OnCommandCore(ev, ev.Source);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedCommand(CommandTriggerObservationEvent observation)
        {
            if (!Config.MatchesEventSource(observation.Command.Source, observation.Holder))
                yield break;
            foreach (Event result in OnCommandCore(observation.Command, observation.Holder))
                yield return result;
        }

        private IEnumerable<Event> OnCommandCore(CommandEvent ev, Unit holder)
        {
            if (TriggerType != TriggerType.Command)
            {
                yield break;
            }

            if (!MatchesSource(ev.AbilityId, useTargetAsDefault: true))
            {
                yield break;
            }
            if (ChargeRestoreAmount == null && ReferenceEquals(holder, ev.Source) &&
                string.Equals(TargetAbilityId, ev.AbilityId, StringComparison.Ordinal) &&
                ev.AbilityInstanceId.HasValue && TargetInstanceId != ev.AbilityInstanceId)
                yield break;

            if (ev.Source.IsRetired)
            {
                if (ReferenceEquals(holder, ev.Source) && ev.Receiver == ev.Source.EventReceiver &&
                    string.Equals(TargetAbilityId, ev.AbilityId, StringComparison.Ordinal))
                    yield return ev.Context.Factory.NewEvent<CommandExecutionEvent>() with
                    {
                        Command = ev, HandlerId = TargetAbilityId, Status = CommandExecutionStatus.Failure,
                        FailureReason = CommandExecutionFailureReason.SourceNotExist,
                        Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero,
                        CancellationTokenSource = ev.CancellationTokenSource
                    };
                yield break;
            }

            if (!CanTrigger(ev, holder))
            {
                yield break;
            }

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandInstanceId, holder, ev.Target,
                ev.ResolvedTargetPoint, receiver: holder.EventReceiver);
            if (invoke != null)
            {
                yield return invoke;
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDamage(DamageEvent ev) => OnDamageCore(ev, replay: false);

        private IEnumerable<Event> OnDamageCore(DamageEvent ev, bool replay)
        {
            if (TriggerType != TriggerType.DamageSkillHit
                || ev.Source == null
                || ev.Receiver != ev.Source.EventReceiver
                || !ev.HasDamage)
            {
                yield break;
            }

            if (!MatchesSource(ev.DamageAbilityId))
            {
                yield break;
            }

            if (!CanTrigger(ev))
            {
                yield break;
            }

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, ev.Source, ev.Target, ev.TargetObject?.Position ?? ev.Target?.MapObject?.Position);
            if (invoke != null)
            {
                yield return invoke;
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnBeingDamaged(DamageEvent ev) => OnBeingDamagedCore(ev, replay: false);

        private IEnumerable<Event> OnBeingDamagedCore(DamageEvent ev, bool replay)
        {
            if (TriggerType != TriggerType.BeingDamaged
                || ev.Target == null
                || ev.Receiver != ev.Target.EventReceiver
                || !ev.HasDamage)
            {
                yield break;
            }

            if (!MatchesSource(ev.DamageAbilityId))
            {
                yield break;
            }

            if (!CanTrigger(ev))
            {
                yield break;
            }

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, ev.Target, ev.Source, ev.TargetObject?.Position ?? ev.Target.MapObject?.Position);
            if (invoke != null)
            {
                yield return invoke;
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedDamage(DamageTriggerObservationEvent observation)
        {
            DamageEvent ev = observation.Damage;
            Unit holder = observation.Holder;
            if (TriggerType is not (TriggerType.DamageSkillHit or TriggerType.BeingDamaged) ||
                !ev.HasDamage || !MatchesSource(ev.DamageAbilityId) ||
                !Config.MatchesEventSource(ev.Source, holder) || !CanTrigger(ev, holder))
                yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, holder, ev.Target,
                ev.TargetObject?.Position ?? ev.Target?.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnProjectileHit(ProjectileHitEvent ev)
        {
            if (ev is ProjectileExplosionEvent || TriggerType != TriggerType.ProjectileHit
                || ev.Source == null
                || ev.Receiver != ev.Source.EventReceiver
                || !MatchesSource(ev.AbilityId))
            {
                yield break;
            }

            if (!CanTrigger(ev))
            {
                yield break;
            }

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, ev.Source, ev.Target, ev.HitPoint);
            if (invoke != null)
            {
                yield return invoke;
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedProjectileHit(ProjectileHitTriggerObservationEvent observation)
        {
            ProjectileHitEvent ev = observation.Hit;
            Unit holder = observation.Holder;
            if (TriggerType != TriggerType.ProjectileHit || !MatchesSource(ev.AbilityId) ||
                !Config.MatchesEventSource(ev.Source, holder) || !CanTrigger(ev, holder))
                yield break;

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(
                ev, ev.CommandEventId, holder, ev.Target, ev.HitPoint, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnProjectileExplosion(ProjectileExplosionEvent ev)
        {
            if (TriggerType != TriggerType.ProjectileExplosion ||
                ev.Receiver != ev.Source.EventReceiver || !MatchesSource(ev.AbilityId) ||
                !CanTrigger(ev)) yield break;
            var invoke = CreateInvokeEvent(ev, ev.CommandEventId, ev.Source, null, ev.HitPoint);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedProjectileExplosion(ProjectileExplosionTriggerObservationEvent observation)
        {
            ProjectileExplosionEvent ev = observation.Explosion;
            Unit holder = observation.Holder;
            if (TriggerType != TriggerType.ProjectileExplosion || !MatchesSource(ev.AbilityId) ||
                !Config.MatchesEventSource(ev.Source, holder) || !CanTrigger(ev, holder))
                yield break;
            var invoke = CreateInvokeEvent(ev, ev.CommandEventId, holder, null, ev.HitPoint,
                receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDamageLifeReduce(DamageLifeReduceEvent ev)
        {
            if (TriggerType != TriggerType.TargetLifeReduce
                || ev.Source == null
                || ev.Receiver != ev.Source.EventReceiver
                || ev.LifePointReduce <= 0)
            {
                yield break;
            }

            if (!MatchesSource(ev.DamageAbility.Ability?.DefinitionId))
            {
                yield break;
            }

            if (!CanTrigger(ev))
            {
                yield break;
            }

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, ev.Source, ev.Target, ev.TargetObject?.Position ?? ev.Target?.MapObject?.Position);
            if (invoke != null)
            {
                yield return invoke;
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedDamageLifeReduce(DamageLifeReduceTriggerObservationEvent observation)
        {
            DamageLifeReduceEvent ev = observation.Reduction;
            Unit holder = observation.Holder;
            if (TriggerType != TriggerType.TargetLifeReduce || ev.Target == null || ev.LifePointReduce <= 0 ||
                !MatchesSource(ev.DamageAbility.Ability?.DefinitionId) ||
                !Config.MatchesEventSource(ev.Source, holder) || !CanTrigger(ev, holder))
                yield break;
            var invoke = CreateInvokeEvent(ev, ev.CommandEventId, holder, ev.Target,
                ev.TargetObject?.Position ?? ev.Target.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnHeal(HealEvent ev)
        {
            if (TriggerType != TriggerType.Heal
                || ev.Source == null
                || ev.Receiver != ev.Source.EventReceiver
                || !MatchesSource(ev.HealAbilityId))
            {
                yield break;
            }

            if (!CanTrigger(ev))
            {
                yield break;
            }

            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, ev.Source, ev.Target, ev.TargetObject?.Position ?? ev.Target.MapObject?.Position);
            if (invoke != null)
            {
                yield return invoke;
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedHeal(HealTriggerObservationEvent observation)
        {
            HealEvent ev = observation.Heal;
            Unit holder = observation.Holder;
            if (TriggerType != TriggerType.Heal || !MatchesSource(ev.HealAbilityId) ||
                !Config.MatchesEventSource(ev.Source, holder) || !CanTrigger(ev, holder))
                yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId, holder, ev.Target,
                ev.TargetObject?.Position ?? ev.Target.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPendingDeath(DeathEvent ev)
        {
            if (TriggerType == TriggerType.Death && Config.DeathPhase == DeathTriggerPhase.Pending)
                throw new InvalidOperationException("Death triggers require published events and indexed routing.");
            yield break;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedPendingDeath(PendingDeathTriggerObservationEvent observation)
        {
            DeathEvent ev = observation.Attempt;
            Unit holder = observation.Holder;
            if (TriggerType != TriggerType.Death || Config.DeathPhase != DeathTriggerPhase.Pending ||
                !MatchesSource(ev.DamageAbility.Ability?.DefinitionId) ||
                !Config.MatchesEventSource(ev.Killer, holder))
                yield break;

            if (!CanTrigger(ev, holder)) yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.AttemptId, holder, ev.Victim,
                ev.TargetObject?.Position ?? ev.Victim.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnSignal(AbilitySignalEvent ev) => OnSignalCore(ev, ev.Source ?? ev.Target);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedSignal(SignalTriggerObservationEvent observation) =>
            OnSignalCore(observation.Signal, observation.Holder);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnUnitLevelUp(UnitLevelUpEvent ev) => OnUnitLevelUpCore(ev, ev.Unit);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedUnitLevelUp(UnitLevelUpTriggerObservationEvent observation) =>
            OnUnitLevelUpCore(observation.Upgrade, observation.Holder);

        private IEnumerable<Event> OnUnitLevelUpCore(UnitLevelUpEvent ev, Unit holder)
        {
            if (TriggerType != TriggerType.UnitLevelUp || !MatchesSource(null) ||
                !Config.MatchesEventSource(ev.Unit, holder) || !CanTrigger(ev, holder))
                yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.OperationId,
                holder, ev.Unit, ev.Unit.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        private IEnumerable<Event> OnSignalCore(AbilitySignalEvent ev, Unit holder)
        {
            if (TriggerType != TriggerType.Signal || string.IsNullOrWhiteSpace(Config.SignalId) ||
                !string.Equals(Config.SignalId, ev.SignalId, StringComparison.Ordinal) ||
                !MatchesSource(ev.AbilitySnapshot?.DefinitionId) ||
                !Config.MatchesEventSource(ev.Source, holder))
                yield break;

            if (!CanTrigger(ev, holder)) yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.CommandEventId,
                holder, ev.Target, ev.Target.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDeath(DeathResolvedEvent ev)
        {
            if (TriggerType == TriggerType.Death && Config.DeathPhase == DeathTriggerPhase.Confirmed)
                throw new InvalidOperationException("Death triggers require published events and indexed routing.");
            yield break;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPublishedDeath(ConfirmedDeathTriggerObservationEvent observation)
        {
            DeathResolvedEvent ev = observation.Death;
            Unit holder = observation.Holder;
            if (TriggerType != TriggerType.Death || Config.DeathPhase != DeathTriggerPhase.Confirmed ||
                !MatchesSource(ev.DamageAbility?.Ability?.DefinitionId) ||
                !Config.MatchesEventSource(ev.Killer, holder))
                yield break;

            if (!CanTrigger(ev, holder)) yield break;
            AbilityTriggerInvokeEvent? invoke = CreateInvokeEvent(ev, ev.DeathId, holder, ev.Victim,
                ev.TargetObject?.Position ?? ev.Victim.MapObject?.Position, receiver: holder.EventReceiver);
            if (invoke != null) yield return invoke;
        }

        private AbilityTriggerInvokeEvent? CreateInvokeEvent(
            Event upstream, Guid commandEventId, Unit? source, Unit? target, Vector2D? hitPoint,
            string? abilityId = null, EventReceiver? receiver = null)
        {
            AbilityTriggerChain chain = upstream.Causality as AbilityTriggerChain ??
                throw new InvalidOperationException("The context must use a TriggerChainFactory for ability triggers.");
            string invokedAbilityId = abilityId ?? TargetAbilityId;
            AbilityTriggerChain? nextChain = chain.Append(invokedAbilityId);
            if (nextChain == null)
            {
                return null;
            }

            AbilityTriggerInvokeEvent invocation;
            if (ChargeRestoreAmount is { } amount)
            {
                Unit holder = source ?? throw new InvalidOperationException("A charge restore requires a holder.");
                Guid instanceId = TargetInstanceId ??
                    throw new InvalidOperationException("A charge restore trigger requires a bound ability instance.");
                AbilityBase ability = holder.EventReceiver.GetHandlers<AbilityBase>()
                    .Single(candidate => candidate.RuntimeInstanceId == instanceId);
                invocation = upstream.Context.Factory.NewEvent<AbilityChargeRestoreInvokeEvent>() with
                {
                    CommandEventId = commandEventId, AbilityId = invokedAbilityId, UpstreamEvent = upstream,
                    Amount = amount,
                    TriggerRegistration = holder.EventReceiver.CaptureTarget(this),
                    AbilityRegistration = holder.EventReceiver.CaptureTarget(ability),
                    AuthorityVersion = ability.Binding.AuthorityVersion,
                    TriggerInstanceId = Binding.InstanceId,
                    TriggerAuthorityVersion = Binding.AuthorityVersion
                };
            }
            else
                invocation = upstream.Context.Factory.NewEvent<AbilityTriggerInvokeEvent>() with
                { CommandEventId = commandEventId, AbilityId = invokedAbilityId, UpstreamEvent = upstream };

            return invocation with
            {
                TargetRuntimeInstanceId = TargetInstanceId,
                TargetAuthorityVersion = Binding.SourceInstanceId.HasValue ? Binding.AuthorityVersion : null,
                IsDirectCommand = ChargeRestoreAmount == null && upstream is CommandEvent direct &&
                    ReferenceEquals(source, direct.Source) &&
                    ReferenceEquals(upstream.Receiver, direct.Source.EventReceiver) &&
                    string.Equals(direct.AbilityId, invokedAbilityId, StringComparison.Ordinal),
                Priority = upstream is DeathEvent or DeathResolvedEvent or AbilitySignalEvent or UnitLevelUpEvent
                    ? upstream.Priority : 0,
                Source = source,
                Target = target,
                CompletionReceiver = upstream switch
                {
                    AbilitySignalEvent signal => signal.CompletionReceiver,
                    ProjectileHitEvent hit => hit.CompletionReceiver,
                    _ => null
                },
                SourceObject = upstream switch
                {
                    CommandEvent command when command.Source == source => command.SourceObject ?? source?.MapObject,
                    CommandEvent => source?.MapObject,
                    ProjectileHitEvent hit when hit.Source == source => hit.SourceObject,
                    ProjectileHitEvent => source?.MapObject,
                    DamageLifeReduceEvent damage when damage.Source == source => damage.SourceObject,
                    DamageLifeReduceEvent damage when damage.Target == source => damage.TargetObject ?? source?.MapObject,
                    DamageLifeReduceEvent => source?.MapObject,
                    HealEvent heal when heal.Source == source => heal.SourceObject,
                    HealEvent heal when heal.Target == source => heal.TargetObject ?? source?.MapObject,
                    HealEvent => source?.MapObject,
                    DamageEvent damage when damage.Source == source => damage.SourceObject,
                    DamageEvent damage when damage.Target == source => damage.TargetObject ?? source?.MapObject,
                    DamageEvent => source?.MapObject,
                    DeathEvent death when death.Killer == source => death.SourceObject,
                    DeathEvent death when death.Victim == source => death.TargetObject ?? source?.MapObject,
                    DeathEvent => source?.MapObject,
                    DeathResolvedEvent death when death.Killer == source => death.SourceObject,
                    DeathResolvedEvent death when death.Victim == source => death.TargetObject ?? source?.MapObject,
                    DeathResolvedEvent => source?.MapObject,
                    _ => source?.MapObject
                },
                TargetObject = upstream switch
                {
                    CommandEvent command => command.TargetObject,
                    ProjectileHitEvent hit => hit.TargetObject,
                    DamageLifeReduceEvent damage when damage.Target == target => damage.TargetObject,
                    DamageLifeReduceEvent damage => damage.SourceObject,
                    HealEvent heal => heal.TargetObject,
                    DamageEvent damage when damage.Target == target => damage.TargetObject,
                    DamageEvent damage when damage.Source == target => damage.SourceObject,
                    DamageEvent => target?.MapObject,
                    DeathEvent death when death.Victim == target => death.TargetObject,
                    DeathEvent death => death.SourceObject,
                    DeathResolvedEvent death when death.Victim == target => death.TargetObject,
                    DeathResolvedEvent death => death.SourceObject,
                    _ => target?.MapObject
                },
                HitPoint = hitPoint,
                TriggerChain = nextChain,
                Receiver = receiver ?? upstream.Receiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = upstream.CancellationTokenSource,
            };
        }

        private bool CanTrigger(Event ev)
        {
            if (ChargeRestoreAmount.HasValue)
                throw new InvalidOperationException("Charge restoration requires an indexed trigger and its holder.");
            if (Config.Conditions.Count != 0)
                throw new InvalidOperationException("Trigger conditions require a published event and its listener holder.");
            return Config.ShouldTrigger(ev.Context.Random);
        }

        private bool CanTrigger(Event ev, Unit holder)
        {
            if (ChargeRestoreAmount.HasValue)
            {
                Guid targetId = TargetInstanceId ??
                    throw new InvalidOperationException("A charge restore trigger requires a bound ability instance.");
                AbilityBase? target = holder.EventReceiver.GetHandlers<AbilityBase>()
                    .SingleOrDefault(candidate => candidate.RuntimeInstanceId == targetId);
                if (target == null) return false;
                if (!string.Equals(target.Id, TargetAbilityId, StringComparison.Ordinal) ||
                    !ReferenceEquals(Binding.Source, target.Binding.Source))
                    throw new InvalidOperationException("The charge restore trigger and ability must share a source and identity.");
                if (Binding.SourceInstanceId != null && !Binding.IsAuthorized(holder.Id) ||
                    target.Binding.SourceInstanceId != null && !target.Binding.IsAuthorized(holder.Id))
                    return false;
                if (target is not IAbilityGate { GateManager.Charges: not null })
                    throw new InvalidOperationException("A charge restore trigger requires a finite-charge ability.");
            }
            if (Config.Conditions.Count > 0)
            {
                var context = new TriggerConditionContext(ev, holder, this);
                bool unknown = false;
                foreach (ITriggerCondition condition in Config.Conditions)
                {
                    bool? result = condition.Evaluate(context);
                    if (result == false) return false;
                    if (!result.HasValue) unknown = true;
                }
                if (unknown) return false;
            }
            return Config.ShouldTrigger(ev.Context.Random);
        }

        private bool MatchesSource(string? abilityId, bool useTargetAsDefault = false)
        {
            return Config.MatchesSource(abilityId, TargetAbilityId, useTargetAsDefault);
        }

    }
}
