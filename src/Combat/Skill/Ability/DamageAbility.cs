using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.Ability
{
    public class DamageAbility : AbilityBase, ICommandAbility
    {
        private static readonly AbilityType TriggerAbilityMask = AbilityType.Attack | AbilityType.Cast | AbilityType.Rune;
        private static readonly AbilityType StandaloneAbilityMask = AbilityType.Standalone;

        public AbilityType AbilityType { get; set; } = AbilityType.Cast | AbilityType.Standalone;
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public TimeSpan? Windup { get; set; }
        public bool OccupiesWindup { get; set; } = true;
        /// <summary>
        /// Target selector controlling which units this ability hits. Null means the
        /// legacy single-target behavior (the command target unit only).
        /// </summary>
        public Miaosoft.Sim.Combat.Skill.Targeting.ITargetSelector? Selector { get; set; }
        /// <summary>
        /// Projectile speed override for direction-targeted abilities. Null uses the
        /// source property's projectile speed (attack abilities).
        /// </summary>
        public double? ProjectileSpeed { get; set; }
        /// <summary>
        /// Null means direct release: skip gate checks, queue reservation, and cooldown.
        /// </summary>
        public CommandGateManager? GateManager { get; set; }
        public List<DamageDefinition>? Damages { get; set; }
        public DamageTick? Tick { get; set; }

        public virtual IEnumerable<Event> CreateDamageFlow(
            Event ev,
            Unit source,
            Unit target,
            TimeSpan timeSpan,
            CancellationTokenSource? cancellationTokenSource)
        {
            DamageProfile damage = DamageProfile.From(this);
            foreach (Event next in DamageFlow.Create(ev, damage, source, target, timeSpan,
                cancellationTokenSource, ProjectileSpeed))
                yield return next;
        }

        [Handler]
        public IEnumerable<Event> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.UpstreamEvent is ScheduledDamageTickEvent)
                yield break;
            if (!string.Equals(ev.AbilityId, Id, StringComparison.Ordinal))
            {
                yield break;
            }

            if (ev.Source == null)
            {
                yield break;
            }

            CommandEvent? directCommand = ev.DirectCommand;
            if (!ev.Source.CanParticipate)
            {
                if (directCommand != null)
                    yield return CreateExecutionEvent(directCommand, CommandExecutionStatus.Failure,
                        CommandExecutionFailureReason.SourceNotExist);
                else
                    yield return TriggeredAbilityGate.Result(this, ev, Guid.NewGuid(), CommandExecutionStatus.Failure,
                        CommandExecutionFailureReason.SourceNotExist);
                yield break;
            }

            if (directCommand is { } command)
            {
                foreach (Event next in HandleCommandTrigger(command)) yield return next;
                yield break;
            }

            if (GateManager != null)
            {
                foreach (Event next in HandleGatedTrigger(ev)) yield return next;
                yield break;
            }

            CommandExecutionFailureReason triggerFailure = PrepareTriggeredDamage(ev, out IReadOnlyList<Unit> targets);
            if (triggerFailure != CommandExecutionFailureReason.None)
            {
                yield return TriggeredAbilityGate.Result(this, ev, Guid.NewGuid(), CommandExecutionStatus.Failure, triggerFailure);
                yield break;
            }

            if (Tick != null)
            {
                foreach (Event next in CreateTriggeredTickFlow(ev, targets))
                {
                    yield return next;
                }
                yield break;
            }

            foreach (Unit target in targets)
            {
                foreach (Event next in CreateDamageFlow(ev, ev.Source!, target, TimeSpan.Zero, new CancellationTokenSource()))
                {
                    yield return next with { IsScopeDetached = true };
                }
            }
        }

        public IEnumerable<Event> HandleCommandTrigger(CommandEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (!DamageAbility.IsCommandAbility(AbilityType))
            {
                yield break;
            }

            if (Binding.SourceInstanceId != null)
            {
                if (!Binding.IsAuthorized(ev.Source.Id))
                {
                    yield return CreateExecutionEvent(ev, CommandExecutionStatus.Failure,
                        CommandExecutionFailureReason.AbilityNotAvailable);
                    yield break;
                }
                GateManager?.BindAuthority(Binding);
            }

            if (!TryPrepareDamage(ev, out CommandExecutionEvent? failureEvent))
            {
                if (failureEvent != null)
                {
                    yield return failureEvent;
                }

                yield break;
            }

            yield return ev.Context.Factory.NewEvent<UnitActionGateRequestEvent>() with
            {
                Source = ev.Source,
                Origin = ev,
                CommandId = ev.CommandInstanceId,
                AbilityId = Id,
                AbilityGate = GateManager,
                Windup = Windup,
                CheckRestrictions = GateManager != null,
                OccupiesChannel = GateManager?.OccupiesWindup == true,
                Casting = Casting,
                AbilityTarget = ev.Source.EventReceiver.CaptureTarget(this),
                ActionCancellation = new CancellationTokenSource(),
                Receiver = ev.Source.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = ev.CancellationTokenSource,
            };
        }

        [Handler]
        public IEnumerable<Event> OnActionGateResult(UnitActionGateResultEvent ev)
        {
            UnitActionGateRequestEvent request = ev.Request;
            if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal))
            {
                yield break;
            }

            if (request.Origin is AbilityTriggerInvokeEvent trigger)
            {
                if (!ev.Result.CanExecute)
                    yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                        CommandExecutionStatus.Failure, ev.Result.FailureReason, ev.Result.NextAvailableTime);
                else
                    yield return TriggeredAbilityGate.Release(ev);
                yield break;
            }

            if (request.Origin is not CommandEvent command) yield break;
            if (!ev.Result.CanExecute)
            {
                yield return CreateExecutionEvent(command, CommandExecutionStatus.Failure,
                    ev.Result.FailureReason, ev.Result.NextAvailableTime);
                yield break;
            }

            yield return ev.Context.Factory.NewEvent<DamageReleaseEvent>() with
            {
                Request = ev.Request,
                Receiver = command.Source.EventReceiver,
                TimeSpan = ev.Result.Windup,
                CancellationTokenSource = ev.Request.ActionCancellation
            };
        }

        private IEnumerable<Event> HandleGatedTrigger(AbilityTriggerInvokeEvent ev)
        {
            Guid activationId = Guid.NewGuid();
            CommandExecutionFailureReason failure = PrepareTriggeredDamage(ev, out _);
            if (failure != CommandExecutionFailureReason.None)
            {
                yield return TriggeredAbilityGate.Result(this, ev, activationId, CommandExecutionStatus.Failure, failure);
                yield break;
            }

            if (Binding.SourceInstanceId != null) GateManager!.BindAuthority(Binding);
            yield return TriggeredAbilityGate.Request(this, ev, activationId, GateManager!, Windup);
        }

        [Handler]
        public IEnumerable<Event> OnTriggeredRelease(TriggeredAbilityReleaseEvent ev)
        {
            UnitActionGateRequestEvent request = ev.Request;
            if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal) ||
                request.Origin is not AbilityTriggerInvokeEvent trigger)
                yield break;

            CommandGateManager gate = request.AbilityGate
                ?? throw new InvalidOperationException("A gated trigger release requires its ability gate.");
            Unit source = request.Source;
            if (trigger.CancellationToken.IsCancellationRequested || request.ActionCancellation.IsCancellationRequested)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            CommandExecutionFailureReason failure = PrepareTriggeredDamage(trigger, out IReadOnlyList<Unit> targets);
            if (failure != CommandExecutionFailureReason.None)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId, CommandExecutionStatus.Failure, failure);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            PeriodicEffectRegistry.PreparedChanges? periodic = null;
            PeriodicEffectRejectionReason rejection = default;
            PeriodicDamageApplication? application = null;
            Event[] effects = [];
            CommandReleaseResult result;
            try
            {
                if (Tick is { } tick)
                {
                    TimeSpan firstTickDelay = tick.FirstTickDelay ?? (Delay > TimeSpan.Zero ? Delay : tick.TickInterval);
                    DamageProfile damage = DamageProfile.From(this);
                    application = new PeriodicDamageApplication
                    {
                        Damage = damage, TickCount = tick.TickCount, TickInterval = tick.TickInterval,
                        FirstTickDelay = firstTickDelay
                    };
                }
                else
                {
                    effects = targets.SelectMany(target => CreateDamageFlow(
                        trigger, source, target, TimeSpan.Zero, new CancellationTokenSource()))
                        .Select(effect => effect with
                        {
                            IsScopeDetached = true, CommittedOperationId = request.ActivationId
                        }).ToArray();
                }
                result = application == null
                    ? gate.CommitRelease(source, request.ActivationId, ev.Context.ContextTimeSpan,
                        request.ActionCancellation.Token, trigger.CancellationToken, ev)
                    : gate.CommitRelease(source, request.ActivationId, ev.Context.ContextTimeSpan,
                        request.ActionCancellation.Token, trigger.CancellationToken, ev, transaction =>
                        {
                            periodic = trigger.TriggerChain.PeriodicEffects.PrepareBatch(
                                transaction, trigger.TriggerChain, source, targets, application,
                                default, out rejection);
                            return periodic != null;
                        });
            }
            catch (Exception error)
            {
                if (periodic != null)
                {
                    if (gate.WasCommitted(request.ActivationId))
                        periodic.Publish();
                    else
                    {
                        try { periodic.Abort(); }
                        catch (Exception cleanupError)
                        {
                            throw new AggregateException("Triggered damage release and cleanup failed.", error, cleanupError);
                        }
                    }
                }
                gate.CancelReservation(request.ActivationId);
                throw;
            }
            if (result != CommandReleaseResult.Committed)
            {
                periodic?.Abort();
                if (result != CommandReleaseResult.Canceled)
                    yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                        CommandExecutionStatus.Failure,
                        result == CommandReleaseResult.EffectRejected
                            ? rejection == PeriodicEffectRejectionReason.Canceled
                                ? throw new InvalidOperationException("A canceled periodic preparation cannot be rejected.")
                                : CommandExecutionFailureReason.PeriodicEffectLimit
                            : TriggeredAbilityGate.Failure(result));
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            periodic?.Publish();
            if (application != null)
                effects = periodic!.Added.SelectMany(effect =>
                    PeriodicEffectRegistry.CreateStartEvents(ev.Context, trigger.CommandEventId,
                        source, effect.Handle.Target, trigger.CompletionReceiver,
                        application, effect.Handle, request.ActivationId)).ToArray();
            if (gate.ResourceCost > 0)
                yield return UnitEnergyChangedEvent.FromUnit(ev.Context, source) with
                { CommittedOperationId = request.ActivationId };
            yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId, CommandExecutionStatus.Success) with
            { CommittedOperationId = request.ActivationId };
            foreach (Event effect in effects) yield return effect;
            foreach (Event next in TriggeredAbilityGate.FinishAfterRelease(ev, request)) yield return next;
        }

        private CommandExecutionFailureReason PrepareTriggeredDamage(
            AbilityTriggerInvokeEvent ev, out IReadOnlyList<Unit> targets)
        {
            _ = RequireDamageDefinitions();
            targets = Array.Empty<Unit>();
            Unit source = ev.Source ?? throw new InvalidOperationException("Triggered damage requires a source.");
            if (!ReferenceEquals(source.EventReceiver.Context, ev.Context) ||
                !ev.Context.ContainsReceiver(source.EventReceiver))
                throw new InvalidOperationException("The damage source must belong to the same context.");
            if (!source.CanParticipate || source.MapObject is { CanCast: false } || source.Abilities?.Property == null)
                return CommandExecutionFailureReason.SourceNotExist;
            if (Binding.SourceInstanceId != null && !Binding.IsAuthorized(source.Id))
                return CommandExecutionFailureReason.AbilityNotAvailable;

            IReadOnlyList<Unit> selected;
            if (Selector != null)
            {
                AbilityTargetingContext targeting = AbilityTargetingContext.FromTrigger(ev);
                if (source.Registry == null || !Selector.Validate(targeting))
                    return CommandExecutionFailureReason.SelectorRejected;
                selected = Selector.ResolveTargets(targeting, source.Registry);
            }
            else if (Tick != null)
                selected = ev.Target == null ? Array.Empty<Unit>() : [ev.Target];
            else
                selected = [ev.Target ?? source];

            if (selected.Any(target => !ReferenceEquals(target.EventReceiver.Context, ev.Context) ||
                !ev.Context.ContainsReceiver(target.EventReceiver)))
                throw new InvalidOperationException("Triggered damage targets must belong to the same context.");
            targets = selected.Where(target => target.CanParticipate && target.Abilities?.Property != null)
                .Distinct<Unit>(ReferenceEqualityComparer.Instance).ToArray();
            return targets.Count == 0 ? CommandExecutionFailureReason.TargetNotExist : CommandExecutionFailureReason.None;
        }

        [Handler]
        public IEnumerable<Event> OnRelease(DamageReleaseEvent ev)
        {
            UnitActionGateRequestEvent request = ev.Request;
            if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal) ||
                request.Origin is not CommandEvent command) yield break;

            if (command.CancellationToken.IsCancellationRequested || request.ActionCancellation.IsCancellationRequested)
            {
                request.AbilityGate?.CancelReservation(request.ActivationId);
            }
            else if (Binding.SourceInstanceId != null && !Binding.IsAuthorized(command.Source.Id))
            {
                request.AbilityGate?.CancelReservation(request.ActivationId);
                yield return CreateExecutionEvent(command, CommandExecutionStatus.Failure,
                    CommandExecutionFailureReason.AbilityNotAvailable);
            }
            else if (!TryPrepareDamage(command, out CommandExecutionEvent? failure))
            {
                request.AbilityGate?.CancelReservation(request.ActivationId);
                if (failure != null) yield return failure;
            }
            else
            {
                PeriodicEffectHandle? periodic = null;
                AbilityLifecycleEvent? periodicStart = null;
                if (Tick is { } tick)
                {
                    if (command.Target == null)
                    {
                        request.AbilityGate?.CancelReservation(request.ActivationId);
                        yield return CreateExecutionEvent(command, CommandExecutionStatus.Failure,
                            CommandExecutionFailureReason.TargetNotExist);
                        yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
                        {
                            Source = command.Source, CommandId = command.CommandInstanceId,
                            ActivationId = request.ActivationId,
                            Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
                        };
                        yield break;
                    }

                    PeriodicEffectRejectionReason rejection;
                    try
                    {
                        AbilityTriggerChain chain = ev.Causality as AbilityTriggerChain
                            ?? throw new InvalidOperationException("The context must use a TriggerChainFactory for periodic effects.");
                        DamageProfile damage = DamageProfile.From(this);
                        periodic = chain.PeriodicEffects.TryReserve(chain, command.Source, command.Target, damage,
                            tick.TickCount, tick.TickInterval, tick.FirstTickDelay ?? Delay, null, command.CancellationToken, null,
                            out rejection, out _);
                        if (periodic != null)
                            periodicStart = CreateDelayedDamageFlow(command, damage, tick, periodic);
                    }
                    catch
                    {
                        if (periodic != null)
                            periodic.Registry.Finish(periodic, cancel: true);
                        request.AbilityGate?.CancelReservation(request.ActivationId);
                        throw;
                    }
                    if (periodic == null)
                    {
                        request.AbilityGate?.CancelReservation(request.ActivationId);
                        if (rejection != PeriodicEffectRejectionReason.Canceled)
                            yield return CreateExecutionEvent(command, CommandExecutionStatus.Failure,
                                CommandExecutionFailureReason.PeriodicEffectLimit);
                        yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
                        {
                            Source = command.Source, CommandId = command.CommandInstanceId,
                            ActivationId = request.ActivationId,
                            Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
                        };
                        yield break;
                    }
                }

                if (request.AbilityGate is { } gate)
                {
                    CommandReleaseResult result;
                    try
                    {
                        result = gate.CommitRelease(command.Source, request.ActivationId,
                            ev.Context.ContextTimeSpan, request.ActionCancellation.Token, command.CancellationToken, ev);
                    }
                    catch
                    {
                        if (periodic != null && !gate.WasCommitted(request.ActivationId))
                            periodic.Registry.Finish(periodic, cancel: true);
                        throw;
                    }
                    if (result != CommandReleaseResult.Committed)
                    {
                        if (periodic != null)
                            periodic.Registry.Finish(periodic, cancel: true);
                        if (result != CommandReleaseResult.Canceled)
                        {
                            CommandExecutionFailureReason reason = result switch
                            {
                                CommandReleaseResult.ResourceInsufficient => CommandExecutionFailureReason.ResourceInsufficient,
                                CommandReleaseResult.AbilityNotAvailable => CommandExecutionFailureReason.AbilityNotAvailable,
                                CommandReleaseResult.Casting => CommandExecutionFailureReason.Casting,
                                CommandReleaseResult.CoolingDown => CommandExecutionFailureReason.CoolingDown,
                                CommandReleaseResult.ChargesDepleted => CommandExecutionFailureReason.ChargesDepleted,
                                _ => throw new InvalidOperationException("Unexpected release result.")
                            };
                            yield return CreateExecutionEvent(command, CommandExecutionStatus.Failure, reason);
                        }
                        yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
                        {
                            Source = command.Source, CommandId = command.CommandInstanceId,
                            ActivationId = request.ActivationId,
                            Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
                        };
                        yield break;
                    }
                    if (gate.ResourceCost > 0)
                        yield return UnitEnergyChangedEvent.FromUnit(ev.Context, command.Source);
                }
                else if (periodic != null && !ev.CommittedOperationId.HasValue)
                    ev.Context.MarkCurrentOperationCommitted(request.ActivationId);
                yield return CreateExecutionEvent(command, CommandExecutionStatus.Success);
                if (periodicStart != null)
                    yield return periodicStart;
                else
                    foreach (Event next in CreateSelectedDamageFlow(command, Delay)) yield return next;

                if (request.Casting is { } casting && casting.ChannelDuration > TimeSpan.Zero)
                {
                    yield return ev.Context.Factory.NewEvent<CastChannelStartedEvent>() with
                    {
                        Request = request, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
                    };
                    yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
                    {
                        Source = command.Source, CommandId = command.CommandInstanceId,
                        ActivationId = request.ActivationId,
                        Receiver = ev.Receiver, TimeSpan = casting.ChannelDuration,
                        CancellationTokenSource = request.ActionCancellation
                    };
                    yield break;
                }
            }
            yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
            {
                Source = command.Source, CommandId = command.CommandInstanceId,
                ActivationId = request.ActivationId,
                Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
            };
        }

        private IEnumerable<Event> CreateTriggeredProjectileFlow(AbilityTriggerInvokeEvent ev)
        {
            AbilityTargetingContext targetingContext = AbilityTargetingContext.FromTrigger(ev);
            IReadOnlyList<Unit> targets;
            if (Selector == null)
            {
                targets = targetingContext.Target == null ? Array.Empty<Unit>() : new[] { targetingContext.Target };
            }
            else
            {
                UnitSpatialRegistry? registry = ev.Source?.Registry;
                if (registry == null)
                {
                    yield break;
                }

                targets = Selector.ResolveTargets(targetingContext, registry);
            }

            foreach (Unit target in targets)
            {
                Vector2D? targetPoint = target.MapObject?.Position ?? targetingContext.TargetPoint;
                double? distance = ev.Source?.MapObject != null && targetPoint.HasValue
                    ? ev.Source.MapObject.Position.DistanceTo(targetPoint.Value)
                    : null;

                yield return ev.Context.Factory.NewEvent<ProjectileEvent>() with
                {
                    CommandEventId = ev.CommandEventId,
                    AbilityId = Id,
                    AbilitySnapshot = AbilityRuntimeSnapshot.Capture(this),
                    Source = ev.Source!,
                    Target = target,
                    TargetPoint = targetPoint,
                    Distance = distance,
                    ProjectileSpeed = ProjectileSpeed,
                    TriggerChain = ev.TriggerChain,
                    Receiver = ev.Source!.EventReceiver,
                    TimeSpan = TimeSpan.Zero,
                    CancellationTokenSource = new CancellationTokenSource(),
                };
            }
        }

        private IEnumerable<Event> CreateTriggeredTickFlow(AbilityTriggerInvokeEvent ev, IReadOnlyList<Unit> targets)
        {
            Unit source = ev.Source ?? throw new InvalidOperationException("Triggered damage requires a source.");
            DamageTick tick = Tick ?? throw new InvalidOperationException("Tick configuration is required.");
            var application = new PeriodicDamageApplication
            {
                Damage = DamageProfile.From(this), TickCount = tick.TickCount, TickInterval = tick.TickInterval,
                FirstTickDelay = tick.FirstTickDelay ?? (Delay > TimeSpan.Zero ? Delay : tick.TickInterval),
            };
            Guid activationId = Guid.NewGuid();
            PeriodicEffectRegistry.PreparedChanges? periodic = ev.TriggerChain.PeriodicEffects.TryReserveBatch(
                ev.TriggerChain, source, targets, application, default, out PeriodicEffectRejectionReason rejection);
            if (periodic == null)
            {
                if (rejection != PeriodicEffectRejectionReason.Canceled)
                    yield return TriggeredAbilityGate.Result(this, ev, activationId,
                        CommandExecutionStatus.Failure, CommandExecutionFailureReason.PeriodicEffectLimit);
                yield break;
            }

            Event[] effects;
            try
            {
                effects = periodic.Added.SelectMany(effect =>
                    PeriodicEffectRegistry.CreateStartEvents(ev.Context, ev.CommandEventId, source,
                        effect.Handle.Target, ev.CompletionReceiver, application, effect.Handle, activationId)).ToArray();
            }
            catch (Exception error)
            {
                try { periodic.RollbackAdded(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Triggered periodic damage and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            if (ev.CancellationToken.IsCancellationRequested)
            {
                periodic.RollbackAdded();
                yield break;
            }
            try
            {
                if (!ev.CommittedOperationId.HasValue)
                    ev.Context.MarkCurrentOperationCommitted(activationId);
            }
            catch (Exception error)
            {
                try { periodic.RollbackAdded(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Triggered periodic damage commit and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            periodic.Publish();
            yield return TriggeredAbilityGate.Result(this, ev, activationId, CommandExecutionStatus.Success) with
            { CommittedOperationId = activationId };
            foreach (Event effect in effects) yield return effect;
        }

        private IEnumerable<Event> CreateSelectedDamageFlow(CommandEvent ev, TimeSpan damageAt)
        {
            IReadOnlyList<Unit> targets;
            if (Selector == null)
            {
                targets = ev.Target == null ? Array.Empty<Unit>() : new[] { ev.Target };
            }
            else
            {
                Miaosoft.Sim.Combat.Skill.Targeting.UnitSpatialRegistry? registry = ev.Source.Registry;
                if (registry == null)
                {
                    yield return CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SelectorRejected);
                    yield break;
                }

                targets = Selector.ResolveTargets(AbilityTargetingContext.FromCommand(ev), registry);
            }

            double? speed = ProjectileSpeed;

            foreach (Unit target in targets)
            {
                double? distance = ev.Distance
                    ?? (ev.Source.MapObject != null && target.MapObject != null
                        ? ev.Source.MapObject.Position.DistanceTo(target.MapObject.Position)
                        : null);

                yield return ev.Context.Factory.NewEvent<ProjectileEvent>() with
                {
                    CommandEventId = ev.CommandInstanceId,
                    AbilityId = Id,
                    AbilitySnapshot = AbilityRuntimeSnapshot.Capture(this),
                    Source = ev.Source,
                    Target = target,
                    TargetPoint = target.MapObject?.Position,
                    Distance = distance,
                    ProjectileSpeed = speed,
                    TriggerChain = null,
                    Receiver = ev.Source.EventReceiver,
                    TimeSpan = damageAt,
                    CancellationTokenSource = new CancellationTokenSource(),
                };
            }
        }

        [Handler]
        public IEnumerable<Event> OnProjectileHit(ProjectileHitEvent ev)
        {
            if (!string.Equals(ev.AbilityId, Id, StringComparison.Ordinal) ||
                ev.AbilitySnapshot?.RuntimeInstanceId != RuntimeInstanceId)
            {
                yield break;
            }

            if (ev.Receiver != ev.Source.EventReceiver)
            {
                yield break;
            }

            if (ev.Target == null || !ev.Target.CanParticipate)
            {
                yield break;
            }

            foreach (Event next in CreateDamageFlow(ev, ev.Source, ev.Target, TimeSpan.Zero, new CancellationTokenSource()))
            {
                yield return next;
            }
        }

        private bool TryPrepareDamage(
            CommandEvent ev,
            out CommandExecutionEvent? failureEvent)
        {
            failureEvent = null;

            _ = RequireDamageDefinitions();

            if (!ev.Source.CanParticipate || ev.Source.MapObject is { CanCast: false } ||
                ev.ControlVersion.HasValue && ev.ControlVersion != ev.Source.MapObjects.ControlVersion)
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SourceNotExist);
                return false;
            }

            // Selector pre-check runs before entering the cast action queue: a rejected
            // command consumes neither windup nor cooldown.
            AbilityTargetingContext targetingContext = AbilityTargetingContext.FromCommand(ev);
            if (!CommandTargetVisibility.IsVisible(ev, targetingContext))
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.TargetNotExist);
                return false;
            }
            if (Selector != null && !Selector.Validate(targetingContext))
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SelectorRejected);
                return false;
            }

            if (Selector != null && ev.Source.Registry == null)
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SelectorRejected);
                return false;
            }

            if (Selector == null)
            {
                if (ev.Target == null || !ev.Target.CanParticipate)
                {
                    failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.TargetNotExist);
                    return false;
                }

                if (ev.Target.Abilities?.Property == null)
                {
                    failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.TargetNotExist);
                    return false;
                }
            }

            if (ev.Source.Abilities?.Property == null)
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SourceNotExist);
                return false;
            }

            return true;
        }

        private AbilityLifecycleEvent CreateDelayedDamageFlow(
            CommandEvent ev, DamageProfile damage, DamageTick tick, PeriodicEffectHandle effect)
        {
            return ev.Context.Factory.NewEvent<AbilityLifecycleEvent>() with
            {
                CommandEventId = ev.CommandInstanceId,
                Source = ev.Source,
                Target = effect.Target,
                Stage = AbilityLifecycleStage.Start,
                RemainingTicks = tick.TickCount,
                TickInterval = tick.TickInterval,
                TickDamage = damage,
                PeriodicEffect = effect,
                TriggerChain = effect.Chain,
                Receiver = effect.Target.EventReceiver,
                TimeSpan = tick.FirstTickDelay ?? Delay,
                CancellationTokenSource = effect.Cancellation,
                IsScopeDetached = true,
            };
        }

        private CommandExecutionEvent CreateExecutionEvent(
            CommandEvent ev,
            CommandExecutionStatus status,
            CommandExecutionFailureReason failureReason = CommandExecutionFailureReason.None,
            TimeSpan? nextAvailableTime = null)
        {
            return ev.Context.Factory.NewEvent<CommandExecutionEvent>() with
            {
                Command = ev,
                HandlerId = Id,
                Status = status,
                FailureReason = failureReason,
                NextAvailableTime = nextAvailableTime,
                TimeSpan = TimeSpan.Zero,
                Receiver = ev.Receiver,
                CancellationTokenSource = ev.CancellationTokenSource
            };
        }

        private List<DamageDefinition> RequireDamageDefinitions()
        {
            return Damages ?? throw new InvalidOperationException("Damage definitions have not been set.");
        }

        public static bool IsCommandAbility(AbilityType abilityType)
        {
            return IsStandaloneAbility(abilityType)
                && (abilityType & TriggerAbilityMask) != AbilityType.None;
        }

        public static bool IsStandaloneAbility(AbilityType abilityType)
        {
            return (abilityType & StandaloneAbilityMask) != AbilityType.None;
        }

        public static bool IsValidAbilityType(AbilityType abilityType)
        {
            return IsStandaloneAbility(abilityType)
                && (abilityType & TriggerAbilityMask) != AbilityType.None;
        }

    }
}
