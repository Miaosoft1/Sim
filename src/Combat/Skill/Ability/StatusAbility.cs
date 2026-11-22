using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Ability
{
    public class StatusAbility : AbilityBase, IAbilityGate
    {
        private sealed record ApplicationState(int Stack, Unit? Source, StatusApplicationOrigin? Origin);
        private readonly AtomicCell<ApplicationState> application = new(new(1, null, null));
        private readonly PropertyModifierProjection projection = new();

        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public int? Stack { get; set; }
        public int CurrentStack
        {
            get => application.Value.Stack;
            set => application.Update(current => current with { Stack = value });
        }
        public AbilityStackPolicy StackPolicy { get; set; } = AbilityStackPolicy.ExtendAndStack;
        public AbilityTimeBasis TimeBasis { get; set; }
        public bool ProtectedUntilExpiry { get; set; }
        public List<PropertyModifier> Modifiers { get; set; } = new();
        public PropertyRecalculationPolicy RecalculateOn { get; init; } = PropertyRecalculationPolicy.ProjectionChanged;
        public decimal? LifePointMaxMultiplier { get; set; }
        public decimal? EnergyMaxMultiplier { get; set; }
        public ITargetSelector? Selector { get; set; }
        public CommandGateManager? GateManager { get; set; }
        public TimeSpan? Windup { get; set; }
        public bool IsTriggerListener { get; set; }
        public IReadOnlyList<EventData.PeriodicEffectApplication> PeriodicEffects { get; set; } = [];
        public IReadOnlyList<StatusPeriodicEffectDefinition> PeriodicEffectDefinitions { get; init; } = [];
        public string? PayloadId { get; set; }
        public Guid? ApplicationId { get; set; }
        public Unit? ApplicationSource => application.Value.Source;
        public StatusApplicationOrigin? ApplicationOrigin => application.Value.Origin;
        private TimeProvider? realTimeClock;
        private long realTimeStart;

        internal int ReadStack(AtomicTransaction transaction) => transaction.Read(application).Stack;
        internal Unit? ReadApplicationSource(AtomicTransaction transaction) => transaction.Read(application).Source;

        internal void StageApplication(
            AtomicTransaction transaction, int stack, Unit? source, StatusApplicationOrigin? origin) =>
            transaction.Stage(application, new ApplicationState(stack, source, origin));

        public TimeSpan RemainingRealTime
        {
            get
            {
                if (realTimeClock == null)
                    throw new InvalidOperationException("The real-time status lease has not started.");
                TimeSpan remaining = Duration - realTimeClock.GetElapsedTime(realTimeStart);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        internal void StartRealTimeLease(TimeProvider clock, long startedAt)
        {
            realTimeClock = clock;
            realTimeStart = startedAt;
        }

        [Handler]
        public IEnumerable<Event> OnInstalled(AbilityInstalledEvent ev)
        {
            if (!ev.ProjectionAlreadyApplied && ev.Receiver == ev.Target.EventReceiver && ev.AbilityIds.Contains(Id))
            {
                ev.Target.Abilities.ReApply();
            }
            yield break;
        }

        [Handler]
        public IEnumerable<Event> OnUninstalled(AbilityUninstalledEvent ev)
        {
            if (!ev.ProjectionAlreadyApplied && ev.Receiver == ev.Target.EventReceiver && ev.AbilityIds.Contains(Id))
            {
                ev.Target.Abilities.ReApply();
            }
            yield break;
        }

        [Handler]
        public IEnumerable<Event> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (!IsTriggerListener || !string.Equals(ev.AbilityId, Id, StringComparison.Ordinal) ||
                ev.Source == null || ev.Source.IsRetired)
            {
                yield break;
            }

            Guid activationId = Guid.NewGuid();
            CommandExecutionFailureReason failure = StatusTargeting.Prepare(ev, Binding, Selector, out IReadOnlyList<Unit> targets);
            if (failure != CommandExecutionFailureReason.None)
            {
                yield return TriggeredAbilityGate.Result(this, ev, activationId, CommandExecutionStatus.Failure, failure);
                yield break;
            }

            if (GateManager is { } gate)
            {
                if (Binding.SourceInstanceId != null) gate.BindAuthority(Binding);
                yield return TriggeredAbilityGate.Request(this, ev, activationId, gate, Windup);
                yield break;
            }

            CapturedHandlerTarget target = ev.Source.EventReceiver.CaptureTarget(this);
            foreach (Event effect in StatusApplicationRequests.Create(this, ev, targets, target))
                yield return effect;
        }

        [Handler]
        public IEnumerable<Event> OnActionGateResult(UnitActionGateResultEvent ev)
        {
            UnitActionGateRequestEvent request = ev.Request;
            if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal) ||
                request.Origin is not AbilityTriggerInvokeEvent trigger)
                yield break;

            if (!ev.Result.CanExecute)
                yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                    CommandExecutionStatus.Failure, ev.Result.FailureReason, ev.Result.NextAvailableTime);
            else
                yield return TriggeredAbilityGate.Release(ev);
        }

        [Handler]
        public IEnumerable<Event> OnTriggeredRelease(TriggeredAbilityReleaseEvent ev)
        {
            UnitActionGateRequestEvent request = ev.Request;
            if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal) ||
                request.Origin is not AbilityTriggerInvokeEvent trigger)
                yield break;

            CommandGateManager gate = request.AbilityGate ??
                throw new InvalidOperationException("A gated status release requires its ability gate.");
            if (trigger.CancellationToken.IsCancellationRequested || request.ActionCancellation.IsCancellationRequested)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            CommandExecutionFailureReason failure;
            IReadOnlyList<Unit> targets;
            try { failure = StatusTargeting.Prepare(trigger, Binding, Selector, out targets); }
            catch (Exception error)
            {
                try
                {
                    gate.CancelReservation(request.ActivationId);
                    foreach (IEvent _ in ev.Context.Dispatch(
                        TriggeredAbilityGate.Finish(ev, request) with { IsScopeDetached = true })) { }
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Status target validation and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            if (failure != CommandExecutionFailureReason.None)
            {
                gate.CancelReservation(request.ActivationId);
                yield return TriggeredAbilityGate.Result(this, trigger, request.ActivationId,
                    CommandExecutionStatus.Failure, failure);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            AbilityInstallRequestEvent[] effects;
            StatusReleaseBatch? batch = null;
            try
            {
                if (request.Source.EventReceiver.GetHandlers<AbilityInstallHandler>().Count != 1 ||
                    targets.Any(target => target.EventReceiver.GetHandlers<AbilityInstallHandler>().Count != 1))
                    throw new InvalidOperationException("A coordinated status release requires one installer on each participant.");
                batch = new StatusReleaseBatch(this, ev, trigger);
                batch.BeginRound(targets);
                effects = StatusApplicationRequests.Create(this, trigger, targets, request.AbilityTarget ??
                    throw new InvalidOperationException("A gated status release requires its captured ability."), batch).ToArray();
                foreach (Event effect in effects)
                    foreach (IEvent _ in ev.Context.Dispatch(effect)) { }
            }
            catch (Exception error)
            {
                try
                {
                    if (batch != null) batch.Abort(ev.Context);
                    else
                    {
                        gate.CancelReservation(request.ActivationId);
                        foreach (IEvent _ in ev.Context.Dispatch(
                            TriggeredAbilityGate.Finish(ev, request) with { IsScopeDetached = true })) { }
                    }
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Status release preparation and cleanup failed.", error, cleanupError);
                }
                throw;
            }
        }

        internal bool ApplyModifiers(
            AtomicTransaction transaction, PropertyFormulaContext context, int stack, bool newlyRegistered)
            => projection.Apply(transaction, Modifiers, context, stack, FormulaPolicy, RecalculateOn, newlyRegistered);
    }
}
