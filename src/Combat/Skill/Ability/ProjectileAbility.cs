using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Ability
{
    /// <summary>
    /// Delivery / spatial ability that resolves targets and launches projectiles.
    /// When the projectile reaches its destination, BallisticTravelHandler emits
    /// ProjectileHitEvent, which downstream effect abilities (Damage, Heal, Status) observe.
    /// </summary>
    public class ProjectileAbility : AbilityBase, ICommandAbility
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public TimeSpan? Windup { get; set; }
        public bool OccupiesWindup { get; set; } = true;
        public ITargetSelector? Selector { get; set; }
        public double? ProjectileSpeed { get; set; }
        public CommandGateManager? GateManager { get; set; }
        public Map.MovementMode? MovementMode { get; set; }
        public ProjectileHitResponse? HitResponse { get; set; }
        public ProjectileExplosionTrigger ExplosionTrigger { get; set; }
        public int? MaxTargets { get; set; }

        [Handler]
        public IEnumerable<Event> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (!string.Equals(ev.AbilityId, Id, StringComparison.Ordinal) || ev.Source == null)
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

            if (directCommand is { } commandEvent)
            {
                foreach (Event next in HandleCommandTrigger(commandEvent, ev.TriggerChain))
                {
                    yield return next;
                }
                yield break;
            }

            if (GateManager != null)
            {
                Guid activationId = Guid.NewGuid();
                CommandExecutionFailureReason failure = PrepareTriggeredProjectile(ev, out _, out _);
                if (failure != CommandExecutionFailureReason.None)
                    yield return TriggeredAbilityGate.Result(this, ev, activationId, CommandExecutionStatus.Failure, failure);
                else
                {
                    if (Binding.SourceInstanceId != null) GateManager.BindAuthority(Binding);
                    yield return TriggeredAbilityGate.Request(this, ev, activationId, GateManager, Windup);
                }
                yield break;
            }

            CommandExecutionFailureReason triggerFailure = PrepareTriggeredProjectile(
                ev, out AbilityTargetingContext targeting, out IReadOnlyList<Unit> targets);
            if (triggerFailure != CommandExecutionFailureReason.None)
            {
                yield return TriggeredAbilityGate.Result(this, ev, Guid.NewGuid(), CommandExecutionStatus.Failure, triggerFailure);
                yield break;
            }

            Event[] flights = CreateFlights(ev, targeting, targets, ev.CommandEventId, Delay, ev.TriggerChain)
                .Select(flight => flight with { IsScopeDetached = true }).ToArray();
            if (flights.Length == 0)
                yield return TriggeredAbilityGate.Result(this, ev, Guid.NewGuid(), CommandExecutionStatus.Failure,
                    CommandExecutionFailureReason.TargetNotExist);
            else
                foreach (Event flight in flights) yield return flight;
        }

        private IEnumerable<Event> HandleCommandTrigger(CommandEvent ev, AbilityTriggerChain? triggerChain)
        {
            if (ev.CancellationToken.IsCancellationRequested)
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

            AbilityTargetingContext targetingContext = AbilityTargetingContext.FromCommand(ev);
            if (!TryPrepare(ev, targetingContext, out CommandExecutionEvent? failureEvent))
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
                TriggerChain = triggerChain,
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

            yield return ev.Context.Factory.NewEvent<ProjectileLaunchEvent>() with
            {
                AbilityId = Id,
                Command = command,
                Request = request,
                TriggerChain = request.TriggerChain,
                Receiver = command.Source.EventReceiver,
                TimeSpan = ev.Result.Windup,
                CancellationTokenSource = request.ActionCancellation
            };
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
            if (trigger.CancellationToken.IsCancellationRequested || request.ActionCancellation.IsCancellationRequested)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            CommandExecutionFailureReason failure = PrepareTriggeredProjectile(
                trigger, out AbilityTargetingContext targeting, out IReadOnlyList<Unit> targets);
            if (failure != CommandExecutionFailureReason.None)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                    CommandExecutionStatus.Failure, failure);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            Event[] flights;
            try
            {
                flights = CreateFlights(trigger, targeting, targets, trigger.CommandEventId, Delay, trigger.TriggerChain)
                    .Select(flight => flight with { IsScopeDetached = true }).ToArray();
            }
            catch
            {
                gate.CancelReservation(request.ActivationId);
                throw;
            }
            if (flights.Length == 0)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                    CommandExecutionStatus.Failure, CommandExecutionFailureReason.TargetNotExist);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            CommandReleaseResult result = gate.CommitRelease(request.Source, request.ActivationId,
                ev.Context.ContextTimeSpan, request.ActionCancellation.Token, trigger.CancellationToken, ev);
            if (result != CommandReleaseResult.Committed)
            {
                if (result != CommandReleaseResult.Canceled)
                    yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                        CommandExecutionStatus.Failure, TriggeredAbilityGate.Failure(result));
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            if (gate.ResourceCost > 0) yield return UnitEnergyChangedEvent.FromUnit(ev.Context, request.Source);
            yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId, CommandExecutionStatus.Success);
            foreach (Event flight in flights) yield return flight;
            foreach (Event next in TriggeredAbilityGate.FinishAfterRelease(ev, request)) yield return next;
        }

        private CommandExecutionFailureReason PrepareTriggeredProjectile(
            AbilityTriggerInvokeEvent ev, out AbilityTargetingContext targeting, out IReadOnlyList<Unit> targets)
        {
            Unit source = ev.Source ?? throw new InvalidOperationException("A triggered projectile requires a source.");
            targeting = AbilityTargetingContext.FromTrigger(ev);
            targets = Array.Empty<Unit>();
            if (!source.CanParticipate || source.MapObject is { CanCast: false })
                return CommandExecutionFailureReason.SourceNotExist;
            if (Binding.SourceInstanceId != null && !Binding.IsAuthorized(source.Id))
                return CommandExecutionFailureReason.AbilityNotAvailable;

            IReadOnlyList<Unit> selected;
            if (Selector != null)
            {
                if (source.Registry == null || !Selector.Validate(targeting))
                    return CommandExecutionFailureReason.SelectorRejected;
                selected = Selector.ResolveTargets(targeting, source.Registry);
            }
            else
                selected = targeting.Target == null ? Array.Empty<Unit>() : [targeting.Target];

            if (selected.Any(target => !ReferenceEquals(target.EventReceiver.Context, ev.Context) ||
                !ev.Context.ContainsReceiver(target.EventReceiver)))
                throw new InvalidOperationException("Triggered projectile targets must belong to the same context.");
            targets = selected.Where(target => target.CanParticipate).ToArray();
            if (targets.Count == 0 && (targeting.TargetPoint == null ||
                Selector == null && targeting.Target != null || MovementMode == Map.MovementMode.Homing))
                return CommandExecutionFailureReason.TargetNotExist;
            return CommandExecutionFailureReason.None;
        }

        [Handler]
        public IEnumerable<Event> OnLaunch(ProjectileLaunchEvent ev)
        {
            if (ev.AbilityId != Id) yield break;
            var targeting = AbilityTargetingContext.FromCommand(ev.Command);
            bool canceled = ev.Command.CancellationToken.IsCancellationRequested ||
                ev.Request.ActionCancellation.IsCancellationRequested;
            if (canceled)
            {
                ev.Request.AbilityGate?.CancelReservation(ev.Request.ActivationId);
            }
            else if (Binding.SourceInstanceId != null && !Binding.IsAuthorized(ev.Command.Source.Id))
            {
                ev.Request.AbilityGate?.CancelReservation(ev.Request.ActivationId);
                yield return CreateExecutionEvent(ev.Command, CommandExecutionStatus.Failure,
                    CommandExecutionFailureReason.AbilityNotAvailable);
            }
            else if (TryPrepare(ev.Command, targeting, out CommandExecutionEvent? failure))
            {
                if (ev.Request.AbilityGate is { } gate)
                {
                    CommandReleaseResult result = gate.CommitRelease(ev.Command.Source, ev.Request.ActivationId,
                        ev.Context.ContextTimeSpan, ev.Request.ActionCancellation.Token, ev.Command.CancellationToken, ev);
                    if (result != CommandReleaseResult.Committed)
                    {
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
                            yield return CreateExecutionEvent(ev.Command, CommandExecutionStatus.Failure, reason);
                        }
                        yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
                        {
                            Source = ev.Command.Source, CommandId = ev.Command.CommandInstanceId,
                            ActivationId = ev.Request.ActivationId,
                            Receiver = ev.Command.Source.EventReceiver, TimeSpan = TimeSpan.Zero
                        };
                        yield break;
                    }
                    if (gate.ResourceCost > 0)
                        yield return UnitEnergyChangedEvent.FromUnit(ev.Context, ev.Command.Source);
                }
                yield return CreateExecutionEvent(ev.Command, CommandExecutionStatus.Success);
                foreach (Event next in CreateSelectedProjectileFlow(ev.Command, targeting, Delay, ev.TriggerChain))
                {
                    yield return next;
                }
                if (ev.Request.Casting is { } casting && casting.ChannelDuration > TimeSpan.Zero)
                {
                    yield return ev.Context.Factory.NewEvent<CastChannelStartedEvent>() with
                    {
                        Request = ev.Request, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
                    };
                    yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
                    {
                        Source = ev.Command.Source, CommandId = ev.Command.CommandInstanceId,
                        ActivationId = ev.Request.ActivationId,
                        Receiver = ev.Receiver, TimeSpan = casting.ChannelDuration,
                        CancellationTokenSource = ev.Request.ActionCancellation
                    };
                    yield break;
                }
            }
            else
            {
                ev.Request.AbilityGate?.CancelReservation(ev.Request.ActivationId);
                if (failure != null) yield return failure;
            }

            yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
            {
                Source = ev.Command.Source,
                CommandId = ev.Command.CommandInstanceId,
                ActivationId = ev.Request.ActivationId,
                Receiver = ev.Command.Source.EventReceiver,
                TimeSpan = TimeSpan.Zero
            };
        }

        private bool TryPrepare(
            CommandEvent ev,
            AbilityTargetingContext targetingContext,
            out CommandExecutionEvent? failureEvent)
        {
            failureEvent = null;

            if (!ev.Source.CanParticipate || ev.Source.MapObject is { CanCast: false } ||
                ev.ControlVersion.HasValue && ev.ControlVersion != ev.Source.MapObjects.ControlVersion)
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SourceNotExist);
                return false;
            }

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
            if (ev.Target != null && targetingContext.SourceObject != null &&
                (targetingContext.TargetObject is not { IsActive: true, IsInteractable: true, IsSelectable: true } targetBody ||
                    !ReferenceEquals(targetingContext.SourceObject.MapRuntime, targetBody.MapRuntime)))
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SelectorRejected);
                return false;
            }

            if ((Selector == null && ev.Target == null && ev.TargetPoint == null) ||
                MovementMode == Map.MovementMode.Homing && targetingContext.TargetObject == null)
            {
                failureEvent = CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.TargetNotExist);
                return false;
            }

            return true;
        }

        private IEnumerable<Event> CreateSelectedProjectileFlow(
            CommandEvent ev,
            AbilityTargetingContext targetingContext,
            TimeSpan launchAt,
            AbilityTriggerChain? triggerChain)
        {
            IReadOnlyList<Unit> targets;
            if (Selector == null)
            {
                targets = ev.Target == null ? Array.Empty<Unit>() : new[] { ev.Target };
            }
            else
            {
                UnitSpatialRegistry? registry = ev.Source.Registry;
                if (registry == null)
                {
                    yield return CreateExecutionEvent(ev, CommandExecutionStatus.Failure, CommandExecutionFailureReason.SelectorRejected);
                    yield break;
                }

                targets = Selector.ResolveTargets(targetingContext, registry);
            }

            foreach (Event projectile in CreateFlights(ev, targetingContext, targets,
                ev.CommandInstanceId, launchAt, triggerChain)) yield return projectile;
        }

        private IEnumerable<Event> CreateFlights(Event ev, AbilityTargetingContext targeting,
            IReadOnlyList<Unit> targets, Guid commandId, TimeSpan delay, AbilityTriggerChain? chain)
        {
            double speed = ProjectileSpeed ?? 0;
            AbilityRuntimeSnapshot abilitySnapshot = AbilityRuntimeSnapshot.Capture(this);
            Map.MovementMode mode = MovementMode ?? (speed == 0 ? Map.MovementMode.Instant :
                Selector is TrackingSelector ? Map.MovementMode.Homing : Map.MovementMode.Linear);
            Vector2D origin = targeting.SourceObject?.Position ?? Vector2D.Zero;
            if (Selector is LineSelector line)
            {
                Vector2D aim = targeting.TargetPoint ?? origin;
                double length = origin.DistanceTo(aim);
                if (length <= 0) yield break;
                Vector2D direction = new(aim.X - origin.X, aim.Y - origin.Y);
                double worldRange = targeting.SourceObject?.MapRuntime?.TargetingMetric is Map.IMapLineTargetingMetric metric
                    ? metric.ToWorldLineLength(direction, line.Range)
                    : line.Range;
                Vector2D destination = new(origin.X + direction.X * worldRange / length,
                    origin.Y + direction.Y * worldRange / length);
                yield return Flight(null, destination, targets,
                    HitResponse ?? (line.StopAtFirstTarget ? ProjectileHitResponse.Stop : ProjectileHitResponse.Pierce),
                    MaxTargets ?? line.MaxTargets);
            }
            else if (targets.Count == 0 && mode != Map.MovementMode.Homing && targeting.TargetPoint is { } point)
                yield return Flight(null, point, null, HitResponse ?? ProjectileHitResponse.Stop, MaxTargets);
            else
                foreach (Unit target in targets)
                    yield return Flight(target, targeting.ObjectFor(target)?.Position ?? targeting.TargetPoint ?? origin,
                        null, HitResponse ?? ProjectileHitResponse.Stop, MaxTargets);

            ProjectileEvent Flight(Unit? target, Vector2D point, IReadOnlyList<Unit>? selected,
                ProjectileHitResponse response, int? max) => ev.Context.Factory.NewEvent<ProjectileEvent>() with
            {
                CommandEventId = commandId, AbilityId = abilitySnapshot.DefinitionId,
                AbilitySnapshot = abilitySnapshot, Source = targeting.Source,
                Target = target, TargetPoint = point, Distance = origin.DistanceTo(point),
                CompletionReceiver = (ev as AbilityTriggerInvokeEvent)?.CompletionReceiver,
                SourceObject = targeting.SourceObject,
                TargetObject = target == null ? null : targeting.ObjectFor(target),
                ProjectileSpeed = speed, TriggerChain = chain, Receiver = targeting.Source.EventReceiver,
                TimeSpan = delay, CancellationTokenSource = new(), Mode = mode,
                HitResponse = response, MaxTargets = max, ExplosionTrigger = ExplosionTrigger,
                SelectedTargets = selected
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
                CancellationTokenSource = ev.CancellationTokenSource,
            };
        }
    }
}
