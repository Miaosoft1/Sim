using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Map.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler
{
    public sealed class UnitActionGateHandler : Handler, IDisposable
    {
        private readonly Dictionary<string, (EventCancellation Token, TimeSpan ExpiresAt, Guid InstanceId)> abilityLeases =
            new(StringComparer.Ordinal);
        private readonly List<Event> pendingActions = new();
        private bool flushScheduled;
        private TimeSpan reservedUntil;
        private Guid? reservationActivationId;
        private CancellationTokenSource? reservationCancellation;
        private EventScope? reservationScope;
        private CancellationTokenRegistration originCancellation;

        public UnitActionGateHandler()
        {
            Id = nameof(UnitActionGateHandler);
        }

        public ActiveCast? CurrentCast { get; private set; }

        public TimeSpan GetNextAvailableTime(TimeSpan now) => reservedUntil > now ? reservedUntil : now;

        public TimeSpan GetCastForbiddenUntil(TimeSpan now, Unit? source)
        {
            if (source == null)
            {
                return now;
            }

            TimeSpan forbiddenUntil = now;
            foreach (AbilityBase ability in source.EventReceiver.GetHandlers<AbilityBase>())
            {
                if (ability.Labels.HasFlag(AbilityLabel.Stunned)
                    && abilityLeases.TryGetValue(ability.Id, out var lease)
                    && !lease.Token.IsCancellationRequested
                    && lease.ExpiresAt > forbiddenUntil)
                {
                    forbiddenUntil = lease.ExpiresAt;
                }
            }
            return forbiddenUntil;
        }

        public CommandGateCheckResult CheckAvailability(
            TimeSpan now, Unit source, CommandGateManager? abilityGate,
            TimeSpan? windup = null, bool occupiesChannel = true)
        {
            if (!source.CanParticipate || source.ActionGate != this || source.MapObject is { CanCast: false })
            {
                return new(false, TimeSpan.Zero, CommandExecutionFailureReason.SourceNotExist, null);
            }

            if (abilityGate?.ChecksActionRestrictions != false)
            {
                TimeSpan forbiddenUntil = GetCastForbiddenUntil(now, source);
                if (forbiddenUntil > now)
                {
                    return new(false, TimeSpan.Zero, CommandExecutionFailureReason.CastForbidden, forbiddenUntil);
                }

                TimeSpan availableAt = GetNextAvailableTime(now);
                if ((abilityGate?.OccupiesWindup ?? occupiesChannel) && availableAt > now)
                {
                    return new(false, TimeSpan.Zero, CommandExecutionFailureReason.Casting, availableAt);
                }
            }

            if (abilityGate != null)
            {
                return abilityGate.Check(now, windup);
            }

            TimeSpan effectiveWindup = windup.GetValueOrDefault();
            return new(true, effectiveWindup > TimeSpan.Zero ? effectiveWindup : TimeSpan.Zero,
                CommandExecutionFailureReason.None, null);
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnActionRequest(UnitActionGateRequestEvent ev)
            => QueueAction(ev);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnMoveRequest(UnitMoveRequestEvent ev)
            => QueueAction(ev);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInterruptRequest(CastInterruptRequestEvent ev)
            => QueueAction(ev);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnFlush(UnitActionGateFlushEvent ev)
        {
            Event[] actions = pendingActions
                .OrderBy(ActionSequence)
                .ThenBy(action => action.EnqueueSequence)
                .ToArray();
            pendingActions.Clear();
            flushScheduled = false;

            foreach (Event action in actions)
            {
                if (action.CancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                IEnumerable<Event> next = action switch
                {
                    UnitActionGateRequestEvent request => ProcessActionRequest(request),
                    UnitMoveRequestEvent move => ProcessMoveRequest(move),
                    CastInterruptRequestEvent interrupt => ProcessInterruptRequest(interrupt),
                    _ => throw new InvalidOperationException($"Unexpected action request: {action.GetType().Name}.")
                };
                foreach (Event result in next)
                {
                    Event attributed = result.Causality == null
                        ? result with { Causality = action.Causality }
                        : result;
                    yield return attributed.Scope == null && !attributed.IsScopeDetached
                        ? attributed with { Scope = action.Scope }
                        : attributed;
                }
            }
        }

        private IEnumerable<Event> QueueAction(Event ev)
        {
            pendingActions.Add(ev);
            if (!flushScheduled)
            {
                flushScheduled = true;
                // Collect same-time inputs after their validation chains have reached the gate.
                yield return ev.Context.Factory.NewEvent<UnitActionGateFlushEvent>() with
                {
                    Receiver = ev.Receiver,
                    TimeSpan = TimeSpan.Zero,
                    Priority = int.MaxValue,
                    IsScopeDetached = true
                };
            }
        }

        private static long ActionSequence(Event action)
        {
            if (action is UnitActionGateRequestEvent request && request.Origin.EnqueueSequence >= 0)
            {
                return request.Origin.EnqueueSequence;
            }
            return action.EnqueueSequence;
        }

        private IEnumerable<Event> ProcessActionRequest(UnitActionGateRequestEvent ev)
        {
            TimeSpan now = ev.Context.ContextTimeSpan;
            bool occupiesChannel = ev.AbilityGate?.OccupiesWindup ?? ev.OccupiesChannel;
            CommandGateCheckResult result;
            if (!ev.Source.CanParticipate || ev.Receiver != ev.Source.EventReceiver ||
                ev.ControlVersion.HasValue && ev.ControlVersion != ev.Source.MapObjects.ControlVersion ||
                ev.Origin is CommandEvent command && command.ControlVersion.HasValue &&
                command.ControlVersion != ev.Source.MapObjects.ControlVersion)
            {
                result = new(false, TimeSpan.Zero, CommandExecutionFailureReason.SourceNotExist, null);
            }
            else if (ev.AbilityTarget is { IsCurrent: false })
            {
                result = new(false, TimeSpan.Zero, CommandExecutionFailureReason.AbilityNotAvailable, null);
            }
            else if (!ev.CheckRestrictions)
            {
                result = new(true, TimeSpan.Zero, CommandExecutionFailureReason.None, null);
            }
            else
            {
                result = CheckAvailability(now, ev.Source, ev.AbilityGate, ev.Windup, ev.OccupiesChannel);
                if (result.CanExecute && ev.AbilityGate != null)
                {
                    result = ev.AbilityGate.ValidateAndReserve(now, ev.Windup, ev.ActivationId,
                        ev.ActionCancellation.Token, ev.Origin.CancellationToken);
                }
            }

            if (result.CanExecute && ev.CheckRestrictions && occupiesChannel && result.Windup > TimeSpan.Zero)
            {
                ReserveChannel(ev, now + result.Windup);
                if (ev.Casting != null)
                {
                    CurrentCast = new ActiveCast(ev.CommandId, ev.AbilityId, now, reservedUntil,
                        ev.Casting, ev.ActionCancellation)
                    {
                        ActivationId = ev.ActivationId,
                        AbilityGate = ev.AbilityGate, Source = ev.Source,
                        ControlVersion = ev.Source.MapObjects.ControlVersion
                    };
                    yield return ev.Context.Factory.NewEvent<CastStartedEvent>() with
                    {
                        Source = ev.Source,
                        Cast = CurrentCast,
                        Receiver = ev.Source.EventReceiver,
                        TimeSpan = TimeSpan.Zero,
                        CancellationTokenSource = ev.ActionCancellation
                    };
                }
            }

            yield return ev.Context.Factory.NewEvent<UnitActionGateResultEvent>() with
            {
                Request = ev,
                Result = result,
                Receiver = ev.Source.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = ev.CancellationTokenSource,
            };
        }

        private void ReserveChannel(UnitActionGateRequestEvent request, TimeSpan until)
        {
            ClearOriginCancellation();
            reservedUntil = until;
            reservationActivationId = request.ActivationId;
            reservationCancellation = request.ActionCancellation;
            reservationScope = request.Scope;
            EventCancellation token = request.Origin.CancellationToken;
            if (!token.CanBeCanceled) return;

            Context context = request.Context;
            ContextEventInbox inbox = context.Inbox;
            CancellationTokenSource actionCancellation = request.ActionCancellation;
            var canceled = context.Factory.NewEvent<CastCancellationRequestedEvent>() with
            {
                Source = request.Source,
                CommandId = request.CommandId,
                ActivationId = request.ActivationId,
                Receiver = request.Source.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                CorrelationId = request.Origin.CorrelationId
            };
            originCancellation = token.Register(() =>
            {
                try { actionCancellation.Cancel(); }
                finally { inbox.TryPost(canceled); }
            });
        }

        private void ClearOriginCancellation()
        {
            originCancellation.Unregister();
            originCancellation = default;
        }

        private void ReleaseChannel(TimeSpan now)
        {
            ClearOriginCancellation();
            reservationCancellation = null;
            reservationScope = null;
            reservedUntil = now;
            reservationActivationId = null;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCombatScopeStopped(CombatScopeStoppedEvent ev)
        {
            if (ev.Receiver != ev.Unit.EventReceiver) yield break;
            pendingActions.RemoveAll(action => ReferenceEquals(action.Scope, ev.StoppedScope));
            if (!ReferenceEquals(reservationScope, ev.StoppedScope)) yield break;

            if (CurrentCast is { } cast)
                yield return Interrupt(ev, ev.Unit, cast);
            else
            {
                reservationCancellation?.Cancel();
                ReleaseChannel(ev.Context.ContextTimeSpan);
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnLeaseActivated(AbilityLeaseActivatedEvent ev)
        {
            if (ev.Receiver != ev.Target.EventReceiver)
            {
                yield break;
            }

            EventCancellation token = ev.LeaseCancellation;
            if (!token.CanBeCanceled)
                throw new InvalidOperationException("An ability lease must have a cancellation source.");
            if (token.IsCancellationRequested)
                yield break;
            foreach (string abilityId in ev.AbilityIds)
            {
                abilityLeases[abilityId] = (token, ev.ExpiresAt, ev.InstanceId);
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnLeaseInvalidated(AbilityLeaseInvalidatedEvent ev)
        {
            if (ev.Receiver == ev.Target.EventReceiver)
            {
                foreach (AbilityLeaseReference invalidated in ev.Leases)
                {
                    if (abilityLeases.TryGetValue(invalidated.AbilityId, out var lease)
                        && lease.InstanceId == invalidated.InstanceId)
                    {
                        abilityLeases.Remove(invalidated.AbilityId);
                    }
                }
            }
            yield break;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCastStarted(CastStartedEvent ev)
        {
            if (CurrentCast?.ActivationId == ev.Cast.ActivationId && !ev.Cast.Behavior.CanMove)
            {
                yield return ev.Context.Factory.NewEvent<UnitMoveStopEvent>() with
                {
                    Source = ev.Source,
                    ControlVersion = ev.Source.MapObjects.ControlVersion,
                    Receiver = ev.Receiver,
                    TimeSpan = TimeSpan.Zero
                };
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnChannelStarted(CastChannelStartedEvent ev)
        {
            UnitActionGateRequestEvent request = ev.Request;
            if (request.ActionCancellation.IsCancellationRequested || request.Casting is not { } behavior ||
                behavior.ChannelDuration <= TimeSpan.Zero || ev.Receiver != request.Source.EventReceiver ||
                !(request.AbilityGate?.OccupiesWindup ?? request.OccupiesChannel))
                yield break;
            TimeSpan now = ev.Context.ContextTimeSpan;
            if (reservationActivationId == request.ActivationId)
                reservedUntil = now + behavior.ChannelDuration;
            else
                ReserveChannel(request, now + behavior.ChannelDuration);
            CurrentCast = new ActiveCast(request.CommandId, request.AbilityId, now, reservedUntil,
                behavior, request.ActionCancellation)
            {
                Phase = CastPhase.Channeling,
                ActivationId = request.ActivationId,
                AbilityGate = request.AbilityGate,
                Source = request.Source,
                ControlVersion = request.Source.MapObjects.ControlVersion
            };
            yield return ev.Context.Factory.NewEvent<CastStartedEvent>() with
            {
                Source = request.Source, Cast = CurrentCast, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = request.ActionCancellation
            };
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnVisibilityChanged(MapVisibilityChangedEvent ev)
        {
            if (CurrentCast is { Source: { } source } cast &&
                ev.Receiver == source.EventReceiver &&
                cast.ControlVersion != source.MapObjects.ControlVersion &&
                ev.Context.ContextTimeSpan < cast.FinishesAt)
                yield return Interrupt(ev, source, cast, controlChanged: true);
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCastFinished(CastFinishedEvent ev)
        {
            if (CurrentCast?.ActivationId == ev.ActivationId)
            {
                CurrentCast = null;
            }
            if (reservationActivationId == ev.ActivationId)
            {
                ReleaseChannel(ev.Context.ContextTimeSpan);
            }
            yield break;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnOriginCancellationRequested(CastCancellationRequestedEvent ev)
        {
            if (ev.Receiver != ev.Source.EventReceiver || reservationActivationId != ev.ActivationId)
                yield break;

            if (CurrentCast is { } cast && cast.ActivationId == ev.ActivationId)
                yield return Interrupt(ev, ev.Source, cast);
            else
            {
                reservationCancellation?.Cancel();
                ReleaseChannel(ev.Context.ContextTimeSpan);
            }
        }

        private IEnumerable<Event> ProcessMoveRequest(UnitMoveRequestEvent ev)
        {
            if (ev is UnitMoveAuthorizedEvent) yield break;
            double speed = ev.Source.Property.MoveSpeed ?? ev.Source.Property.MoveSpeedBase ?? 0;
            if (ev.Source.MapObject?.CanMove != true ||
                ev.ControlVersion.HasValue && ev.ControlVersion != ev.Source.MapObjects.ControlVersion ||
                !double.IsFinite(ev.Destination.X)
                || !double.IsFinite(ev.Destination.Y) || !double.IsFinite(speed) || speed <= 0)
            {
                yield return ActionResult(ev, ev.Source, false, "无法移动：坐标或移动速度无效。");
                yield break;
            }

            if (!ev.Source.CanParticipate) yield break;
            ActiveCast? cast = GetActiveCast(ev.Context.ContextTimeSpan);
            if (cast != null && !cast.Behavior.CanMove)
            {
                if (!cast.Behavior.CanInterrupt)
                {
                    yield return ActionResult(ev, ev.Source, false, "当前施法不允许移动或中断。");
                    yield break;
                }
                yield return Interrupt(ev, ev.Source, cast);
            }

            yield return ev.Context.Factory.NewEvent<UnitMoveAuthorizedEvent>() with
            {
                Source = ev.Source,
                Destination = ev.Destination,
                ControlVersion = ev.ControlVersion,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero
            };
        }

        private IEnumerable<Event> ProcessInterruptRequest(CastInterruptRequestEvent ev)
        {
            ActiveCast? cast = GetActiveCast(ev.Context.ContextTimeSpan);
            if (cast == null || !cast.Behavior.CanInterrupt)
            {
                yield return ActionResult(ev, ev.Source, false,
                    cast == null ? "当前没有可中断的施法。" : "当前施法不可中断。");
                yield break;
            }
            yield return Interrupt(ev, ev.Source, cast);
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDamage(DamageLifeReduceEvent ev)
        {
            if (ev.Receiver == ev.Target?.EventReceiver && ev.LifePointReduce > 0 && ev.Target != null)
            {
                ActiveCast? cast = GetActiveCast(ev.Context.ContextTimeSpan);
                if (cast != null && cast.Behavior.InterruptOnDamage)
                {
                    yield return Interrupt(ev, ev.Target, cast);
                }
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDeath(DeathResolvedEvent ev)
        {
            if (ev.Receiver == ev.Victim.EventReceiver && reservationActivationId != null)
            {
                if (CurrentCast != null) CurrentCast.Cancel();
                else reservationCancellation?.Cancel();
                CurrentCast = null;
                ReleaseChannel(ev.Context.ContextTimeSpan);
            }
            yield break;
        }

        private ActiveCast? GetActiveCast(TimeSpan now)
            => CurrentCast is { } cast && now < cast.FinishesAt ? cast : null;

        private Event Interrupt(Event ev, Unit source, ActiveCast cast, bool controlChanged = false)
        {
            cast.Cancel();
            CurrentCast = null;
            if (reservationActivationId == cast.ActivationId)
                ReleaseChannel(ev.Context.ContextTimeSpan);
            return ev.Context.Factory.NewEvent<CastInterruptedEvent>() with
            {
                Source = source,
                Cast = cast,
                ControlChanged = controlChanged,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero
            };
        }

        private static Event ActionResult(Event ev, Unit source, bool success, string message)
            => ev.Context.Factory.NewEvent<UnitActionResultEvent>() with
            {
                Source = source,
                Success = success,
                Message = message,
                Receiver = source.EventReceiver,
                TimeSpan = TimeSpan.Zero
            };

        public void Dispose()
        {
            ClearOriginCancellation();
            CurrentCast?.Cancel();
            CurrentCast = null;
            reservationCancellation?.Cancel();
            reservationCancellation = null;
            reservationScope = null;
            reservationActivationId = null;
            reservedUntil = TimeSpan.Zero;
            pendingActions.Clear();
            abilityLeases.Clear();
            flushScheduled = false;
        }
    }
}
