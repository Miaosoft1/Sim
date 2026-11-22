using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Execution;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Handler
{
    /// <summary>
    /// Preset ability that manages install/uninstall of all abilities on a unit.
    /// It is registered on every unit at creation and is never installed/uninstalled itself.
    /// Install/uninstall is executed through context events; nothing outside this handler
    /// mounts handlers directly.
    /// </summary>
    public partial class AbilityInstallHandler : Handler, IDisposable
    {
        // Availability must be updated before install/uninstall completion is observed.
        private const int LeaseNotificationPriority = -1;
        private LeaseState leaseState = new();
        private Dictionary<string, Guid> activeLeases => leaseState.Active;
        private Dictionary<string, RealTimeLease> realTimeLeases => leaseState.RealTime;
        private Dictionary<Guid, SimulationLease> simulationLeases => leaseState.Simulation;

        internal sealed record RealTimeLease(
            Unit? Source, Unit Target, Guid CommandId, string AbilityId, Guid InstanceId,
            long StartedAt, TimeSpan Duration, ITimer Timer, EventScope Scope, CancellationToken RequestToken)
        {
            public CancellationTokenRegistration CancellationRegistration { get; set; }
        }

        internal sealed class SimulationLease(
            EventScope scope, HashSet<string> abilityIds, CancellationToken requestToken, Unit? source, Guid commandId,
            TimeSpan? expiresAt)
        {
            public EventScope Scope { get; } = scope;
            public HashSet<string> AbilityIds { get; } = abilityIds;
            public CancellationToken RequestToken { get; } = requestToken;
            public Unit? Source { get; } = source;
            public Guid CommandId { get; } = commandId;
            public TimeSpan? ExpiresAt { get; } = expiresAt;
            public CancellationTokenRegistration CancellationRegistration { get; set; }
        }

        internal sealed class LeaseState
        {
            public Dictionary<string, Guid> Active { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, RealTimeLease> RealTime { get; } = new(StringComparer.Ordinal);
            public Dictionary<Guid, SimulationLease> Simulation { get; } = [];
        }

        private sealed record LeaseRemovalResult(IReadOnlyList<Guid> Completed, IReadOnlyList<Exception> Failures);
        private sealed record RetiredLeases(
            IReadOnlyList<Guid> Completed, IReadOnlyList<RealTimeLease> RealTime, IReadOnlyList<SimulationLease> Simulation);
        private sealed record PreparedRealTimeLease(StatusAbility Status, TimeProvider Clock, RealTimeLease Lease);
        private sealed record InstallationPublication(
            LeaseState LeaseState, bool CommittedLeaseIsIndependent, UnitProperty Before,
            decimal PreviousEnergy, int PreviousEnergyMax, List<IHandler> ToAdd, IHandler[] RemovedHandlers,
            Dictionary<AbilityBase, AbilityLabel> LabelUpdates,
            Dictionary<StatusAbility, (string PayloadId, Guid InstanceId, string Id)> Applications,
            AbilityLeaseReference[] Invalidated, List<PreparedRealTimeLease> PreparedRealTime,
            SimulationLease? Simulation, Guid InstanceId, string[] SimulationIds, bool HasLease,
            CancellationTokenSource? LeaseToken, EventScope? LeaseScope, List<string> InstalledIds,
            PeriodicEffectRegistry.PreparedChanges? PeriodicChanges);
        private sealed record PreparedInstallation(
            InstallationPublication Publication, List<EventScope> Scopes, List<ITimer> Timers,
            CancellationTokenSource? OwnedLeaseToken)
        {
            public void Abort() => CleanupPreparedInstall(
                Scopes, Timers, Publication.PreparedRealTime, Publication.Simulation,
                OwnedLeaseToken, Publication.PeriodicChanges);
        }
        private sealed record InstallationPreparation(
            PreparedInstallation? Prepared, Event? Notification,
            PeriodicEffectRegistry.StatusChange? PendingPeriodic = null);
        private sealed record PreparedStatusTarget(AbilityInstallEvent Installation, InstallationPreparation Preparation);

        public AbilityInstallHandler()
        {
            Id = nameof(AbilityInstallHandler);
        }

        internal void UseLeaseState(LeaseState state)
        {
            if (activeLeases.Count != 0 || realTimeLeases.Count != 0 || simulationLeases.Count != 0)
                throw new InvalidOperationException("An active installer cannot change its lease state.");
            leaseState = state;
        }

        /// <summary>
        /// Ability factory injected at unit creation (preset infrastructure).
        /// </summary>
        public Func<string, int?, IReadOnlyList<IHandler>>? AbilityFactory { get; set; }

        public bool HasExpiredRealTimeLeases(TimeProvider clock) =>
            realTimeLeases.Values.Any(lease => clock.GetElapsedTime(lease.StartedAt) >= lease.Duration);

        public bool HasCanceledSimulationLeases =>
            simulationLeases.Values.Any(lease => lease.RequestToken.IsCancellationRequested);

        public bool TryGetSimulationLeaseExpiration(string registrationId, out TimeSpan? expiresAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);
            if (activeLeases.TryGetValue(registrationId, out Guid leaseId) &&
                simulationLeases.TryGetValue(leaseId, out SimulationLease? lease))
            {
                expiresAt = lease.ExpiresAt;
                return true;
            }
            expiresAt = null;
            return false;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnAtomicAbilityChange(AtomicAbilityChangeEvent ev)
        {
            if (ev.Changes.SelectMany(change => change.Remove.Concat(change.Add))
                .OfType<IAbilityRuntimePart>().Any(part => part.Binding.Source is AbilitySourceInstance))
                throw new InvalidOperationException("Managed source parts must change through a source change event.");
            foreach (AtomicUnitAbilityChange change in ev.Changes)
                RejectManagedDefinitionChanges(change.Target, change.Add.Select(handler => handler.Id));
            foreach (Event notification in ApplyAtomicAbilityChange(ev)) yield return notification;
        }

        private IEnumerable<Event> ApplyAtomicAbilityChange(AtomicAbilityChangeEvent ev, bool allowNoUnitChanges = false)
        {
            if (ev.Receiver != ev.Target.EventReceiver)
                throw new InvalidOperationException("The ability change must be handled by its target unit.");
            if (!ev.Context.IsProcessingEvent || ev.OperationId == Guid.Empty ||
                ev.CommittedOperationId.HasValue || ev.Completion.CommittedOperationId.HasValue)
                throw new InvalidOperationException("The operation cannot be committed from this event.");
            if (!allowNoUnitChanges && ev.Changes.Count == 0 ||
                ev.Changes.Select(change => change.Target).Distinct(ReferenceEqualityComparer.Instance).Count() != ev.Changes.Count)
                throw new InvalidOperationException("The changed units must be nonempty and distinct.");
            var additions = new HashSet<IHandler>(ReferenceEqualityComparer.Instance);
            foreach (AtomicUnitAbilityChange change in ev.Changes)
            {
                if (!ev.Context.ContainsReceiver(change.Target.EventReceiver) ||
                    !ReferenceEquals(change.Target.EventReceiver.Context, ev.Context))
                    throw new InvalidOperationException("A changed unit does not belong to this context.");
                if (change.Remove.Any(IsProtectedPreset))
                    throw new InvalidOperationException("A preset ability handler cannot be removed.");
                foreach (IHandler handler in change.Add)
                {
                    if (IsProtectedPreset(handler))
                        throw new InvalidOperationException("A preset ability handler cannot be installed.");
                    if (!additions.Add(handler))
                        throw new InvalidOperationException("An instance cannot be installed on multiple units.");
                    if (handler is IAbilityRuntimePart part)
                        part.Binding.StageContext(ev.Transaction, ev.Context);
                    foreach (EventReceiver receiver in ev.Context.FindHandlerReceivers(handler))
                    {
                        if (!ev.Changes.Any(other => ReferenceEquals(other.Target.EventReceiver, receiver) &&
                            other.Remove.Any(removed => ReferenceEquals(removed, handler))))
                            throw new InvalidOperationException("An instance is still registered on another unit.");
                    }
                }
            }
            ValidateChargeGates(ev.Context, additions.Concat(ev.UnregisteredAbilities));

            var invalidations = new List<(LeaseState State, AbilityLeaseReference[] Leases)>();
            var before = new Dictionary<Unit, UnitProperty>();
            foreach (AtomicUnitAbilityChange change in ev.Changes)
            {
                before.Add(change.Target, change.Target.Property.Copy());
                IReadOnlyList<IHandler> proposed = change.Target.EventReceiver.StageHandlerChanges(
                    ev.Transaction, change.Remove, change.Add);
                change.Target.Abilities.StageReApply(ev.Transaction, proposed);
                HashSet<string> removedIds = change.Remove.Select(handler => handler.Id).ToHashSet(StringComparer.Ordinal);
                LeaseState state = change.Target.Abilities.Leases;
                AbilityLeaseReference[] invalidated = state.Active
                    .Where(pair => removedIds.Contains(pair.Key))
                    .Select(pair => new AbilityLeaseReference(pair.Key, pair.Value))
                    .ToArray();
                StageLeaseScopeCancellations(ev.Transaction, state, invalidated);
                invalidations.Add((state, invalidated));
            }

            if (ev.Causality is AbilityTriggerChain triggerChain)
                triggerChain.Router.StageChanges(ev.Transaction, ev.Changes.Select(change =>
                    new TriggerRoutingChange(change.Target, change.Remove, change.Add)).ToArray());
            PeriodicEffectRegistry.PreparedChanges? periodicChanges = PrepareStatusRemoval(
                ev, ev.Transaction, ev.Target, ev.Changes.SelectMany(change => change.Remove).OfType<StatusAbility>());
            ev.Transaction.Commit();
            ev.Context.MarkCurrentOperationCommitted(ev.OperationId);
            ev.Completion.CommittedOperationId = ev.OperationId;
            var failures = new List<Exception>();
            foreach (AtomicUnitAbilityChange change in ev.Changes)
                foreach (IHandler handler in change.Remove.Where(handler => !additions.Contains(handler)))
                    try { DetachChargeGates([handler]); }
                    catch (Exception error) { failures.Add(error); }
            foreach (AtomicUnitAbilityChange change in ev.Changes)
                foreach (IHandler handler in change.Add)
                    try { EnterChargeGates(ev.Context, change.Target, [handler]); }
                    catch (Exception error) { failures.Add(error); }
            foreach (AbilityBase ability in ev.UnregisteredAbilities)
                if (ability is IAbilityGate { GateManager: { } gate })
                    try
                    {
                        if (ability.Binding.SourceInstanceId != null) gate.BindAuthority(ability.Binding);
                        gate.EnterContext(ev.Context, ability);
                    }
                    catch (Exception error) { failures.Add(error); }
            try { periodicChanges?.Publish(); }
            catch (Exception error) { failures.Add(error); }
            RetiredLeases[] retired = invalidations
                .Select(change => DetachLeaseRecords(change.State, change.Leases)).ToArray();
            if (periodicChanges != null)
                failures.AddRange(periodicChanges.CloseRetired());
            foreach (RetiredLeases retirement in retired)
                failures.AddRange(CleanupRetiredLeases(retirement));
            foreach (AtomicUnitAbilityChange change in ev.Changes)
                try { change.Target.Abilities.PublishProjection(); }
                catch (Exception error) { failures.Add(error); }
            var notifications = new List<Event>();
            for (int index = 0; index < invalidations.Count; index++)
            {
                if (invalidations[index].Leases.Length == 0)
                    continue;
                notifications.Add(CreateInvalidationEvent(
                    ev, ev.Changes[index].Target, invalidations[index].Leases, retired[index].Completed)
                    with { IsScopeDetached = true, CommittedOperationId = ev.OperationId });
            }
            foreach (AtomicUnitAbilityChange change in ev.Changes)
            {
                string[] removedIds = change.Remove.OfType<AbilityBase>().Select(ability => ability.Id).ToArray();
                string[] addedIds = change.Add.OfType<AbilityBase>().Select(ability => ability.Id).ToArray();
                if (removedIds.Length > 0)
                    notifications.Add(ev.Context.Factory.NewEvent<AbilityUninstalledEvent>() with
                    {
                        CommandEventId = ev.OperationId, Source = change.Target, Target = change.Target,
                        AbilityIds = removedIds, ProjectionAlreadyApplied = true,
                        Receiver = change.Target.EventReceiver, TimeSpan = TimeSpan.Zero,
                        IsScopeDetached = true, CommittedOperationId = ev.OperationId
                    });
                if (addedIds.Length > 0)
                    notifications.Add(ev.Context.Factory.NewEvent<AbilityInstalledEvent>() with
                    {
                        CommandEventId = ev.OperationId, Source = change.Target, Target = change.Target,
                        AbilityIds = addedIds, ProjectionAlreadyApplied = true,
                        Receiver = change.Target.EventReceiver, TimeSpan = TimeSpan.Zero,
                        IsScopeDetached = true, CommittedOperationId = ev.OperationId
                    });
                UnitProperty old = before[change.Target];
                UnitProperty current = change.Target.Property;
                if (old.LifePoint != current.LifePoint || old.LifePointMax != current.LifePointMax)
                    notifications.Add(UnitLifeChangedEvent.FromUnit(ev.Context, change.Target,
                        old.LifePoint ?? old.LifePointMax) with
                    { IsScopeDetached = true, CommittedOperationId = ev.OperationId });
                if (old.Energy != current.Energy || old.EnergyMax != current.EnergyMax)
                    notifications.Add(UnitEnergyChangedEvent.FromUnit(ev.Context, change.Target) with
                    { IsScopeDetached = true, CommittedOperationId = ev.OperationId });
            }
            notifications.Add(ev.Completion);
            foreach (Event completion in ev.AdditionalCompletions)
            {
                completion.CommittedOperationId = ev.OperationId;
                notifications.Add(completion);
            }
            CommittedEventPublisher.Dispatch(ev.Context, notifications, failures);
            return [];
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInstallRequest(AbilityInstallRequestEvent ev)
        {
            if (ev.StatusBatch?.IsAborted == true)
                yield break;
            AbilityInstallEvent? install;
            try
            {
                install = CreateInstallation(ev);
                if (install != null && ev.StatusBatch != null)
                {
                    foreach (IEvent _ in ev.Context.Dispatch(install)) { }
                    install = null;
                }
            }
            catch (Exception error)
            {
                try { ev.StatusBatch?.Abort(ev.Context); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Status request and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            if (install != null) yield return install;
        }

        private AbilityInstallEvent? CreateInstallation(AbilityInstallRequestEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            ev.StatusBatch?.ValidateRequest(ev);
            if (!Enum.IsDefined(ev.Purpose))
                throw new ArgumentOutOfRangeException(nameof(ev.Purpose));
            if (!Enum.IsDefined(ev.LeaseCancellationPolicy))
                throw new ArgumentOutOfRangeException(nameof(ev.LeaseCancellationPolicy));
            ArgumentNullException.ThrowIfNull(ev.PeriodicEffects);
            if (ev.Purpose == AbilityInstallPurpose.StatusApplication)
                ValidateStatusApplication(ev.Context, ev.Source, ev.Target, ev.ApplicationOrigin, ev);
            else if (ev.ApplicationOrigin != null || ev.PeriodicEffects.Count > 0)
                throw new InvalidOperationException("Only a status application may declare an origin or periodic effects.");
            if (ev.AbilityIds.Count == 0)
            {
                return null;
            }
            if (ev.Purpose == AbilityInstallPurpose.Definition)
                RejectManagedDefinitionChanges(ev.Target, ev.AbilityIds);

            List<IHandler> handlers = new List<IHandler>();
            if (AbilityFactory != null)
            {
                foreach (string abilityId in ev.AbilityIds)
                {
                    IReadOnlyList<IHandler> created = AbilityFactory(abilityId, ev.Level);
                    foreach (IHandler handler in created)
                    {
                        handlers.Add(handler);
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("An ability factory is required to install abilities by ID.");
            }

            IReadOnlyList<PeriodicEffectApplication> periodicEffects = ev.PeriodicEffects;
            TimeSpan? expireAt = ev.ExpireAt;
            if (ev.Purpose == AbilityInstallPurpose.StatusApplication)
            {
                StatusAbility[] statuses = handlers.OfType<StatusAbility>().ToArray();
                if (ev.AbilityIds.Count != 1 || statuses.Length != 1 ||
                    !statuses[0].IsTriggerListener ||
                    !string.Equals(statuses[0].PayloadId, ev.AbilityIds[0], StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Ability '{string.Join(",", ev.AbilityIds)}' is not a triggered status definition.");
                StatusAbility status = statuses[0];
                if (ev.Level is <= 0 || ev.Level.HasValue && status.Level != ev.Level.Value)
                    throw new InvalidOperationException("The status factory returned an invalid application level.");
                if (ev.Context.FindHandlerReceivers(status).Count != 0)
                    throw new InvalidOperationException("The status factory must create a fresh application.");
                if (!expireAt.HasValue && status.TimeBasis == AbilityTimeBasis.Simulation && status.Duration > TimeSpan.Zero)
                    expireAt = ev.Context.ContextTimeSpan.Add(status.Duration).Subtract(TimeSpan.FromTicks(1));
                if (status.PeriodicEffectDefinitions.Count > 0)
                {
                    if (periodicEffects.Count > 0)
                        throw new InvalidOperationException("A status cannot override its compiled periodic effects.");
                    periodicEffects = status.PeriodicEffectDefinitions
                        .Select(definition => definition.Create(status, ev.ApplicationOrigin?.AbilitySnapshot)).ToArray();
                }
                status.Id = ev.AbilityIds[0];
                status.IsTriggerListener = false;
                status.Selector = null;
                status.GateManager = null;
                status.Windup = null;
                handlers.Clear();
                handlers.Add(status);
            }

            if (ev.AdditionalLabels.HasValue)
            {
                foreach (IHandler handler in handlers)
                {
                    if (handler is AbilityBase ability)
                    {
                        ability.Labels |= ev.AdditionalLabels.Value;
                    }
                }
            }

            var install = ev.Context.Factory.NewEvent<AbilityInstallEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = ev.Target,
                Handlers = handlers,
                Purpose = ev.Purpose,
                ApplicationOrigin = ev.ApplicationOrigin,
                PeriodicEffects = periodicEffects.ToArray(),
                LeaseCancellationPolicy = ev.LeaseCancellationPolicy,
                ExpireAt = expireAt,
                CompletionReceiver = ev.CompletionReceiver,
                Receiver = ev.Target.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = ev.CancellationTokenSource,
                StatusBatch = ev.StatusBatch
            };
            ev.ApplicationOrigin?.BindReleasedInstall(ev, install);
            ev.StatusBatch?.AddExpectedInstallation(ev, install);
            return install;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnReviveRequest(UnitReviveRequestEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested || ev.Receiver != ev.Unit.EventReceiver)
            {
                yield break;
            }

            if (ev.Unit.IsRetired)
            {
                yield return ev.Context.Factory.NewEvent<UnitReviveRejectedEvent>() with
                {
                    Unit = ev.Unit,
                    Reason = UnitReviveRejectionReason.Retired,
                    Receiver = ev.CompletionReceiver ?? ev.Unit.EventReceiver,
                    TimeSpan = TimeSpan.Zero,
                    Priority = ev.Priority
                };
                yield break;
            }
            if (ev.Unit.IsAlive)
            {
                if (ev.CompletionReceiver != null)
                    yield return ev.Context.Factory.NewEvent<UnitReviveSkippedEvent>() with
                    {
                        Unit = ev.Unit, Receiver = ev.CompletionReceiver,
                        TimeSpan = TimeSpan.Zero, Priority = ev.Priority
                    };
                yield break;
            }

            int previousLife = ev.Unit.Property.LifePoint ?? 0;
            if (ev.RestoreLifePoint is int life &&
                (life <= 0 || life > ev.Unit.Property.LifePointMax))
                throw new ArgumentOutOfRangeException(nameof(ev.RestoreLifePoint),
                    "Explicit revival life must be positive and no greater than the unit's maximum life.");
            ev.Unit.SynchronizeEnergy(ev.Context.ContextTimeSpan);
            List<IHandler> runtimeHandlers = [];
            if (ev.Unit.ActionGate == null)
                runtimeHandlers.Add(new UnitActionGateHandler());
            if (ev.Unit.EventReceiver.Get<AbilityLifecycleHandler>() == null)
                runtimeHandlers.Add(new AbilityLifecycleHandler());
            if (runtimeHandlers.Count > 0)
                ev.Unit.EventReceiver.AddHandler(runtimeHandlers.ToArray());

            if (ev.RestoreLifePoint is int restored)
            {
                ev.Unit.Property.LifePoint = restored;
            }
            ev.Unit.MapObjects.Reactivate();
            if (ev.RestoreLifePoint.HasValue)
                yield return UnitLifeChangedEvent.FromUnit(ev.Context, ev.Unit, previousLife);
            yield return ev.Context.Factory.NewEvent<UnitRevivedEvent>() with
            {
                Unit = ev.Unit,
                PreviousLifePoint = previousLife,
                Receiver = ev.CompletionReceiver ?? ev.Unit.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                Priority = ev.Priority
            };
            yield return UnitEnergyChangedEvent.FromUnit(ev.Context, ev.Unit);
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInstall(AbilityInstallEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (ev.Receiver != ev.Target.EventReceiver)
            {
                yield break;
            }

            if (ev.StatusBatch is { } batch)
            {
                if (batch.IsAborted) yield break;
                bool ready;
                try
                {
                    if (ev.Purpose != AbilityInstallPurpose.StatusApplication ||
                        !ReferenceEquals(leaseState, ev.Target.Abilities.Leases))
                        throw new InvalidOperationException("A coordinated status installation requires its initialized target installer.");
                    batch.ValidateInstallation(ev);
                    ValidateStatusApplication(ev.Context, ev.Source, ev.Target, ev.ApplicationOrigin, ev);
                    ready = batch.Add(ev);
                    if (ready)
                    {
                        Event batchReady = ev.Context.Factory.NewEvent<StatusApplicationBatchReadyEvent>() with
                        {
                            Batch = batch, Receiver = batch.Release.Request.Source.EventReceiver,
                            IsScopeDetached = true
                        };
                        foreach (IEvent _ in ev.Context.Dispatch(batchReady)) { }
                    }
                }
                catch (Exception error)
                {
                    try { batch.Abort(ev.Context); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("Status delivery and cleanup failed.", error, cleanupError);
                    }
                    throw;
                }
                yield break;
            }

            var transaction = new AtomicTransaction();
            InstallationPreparation preparation = PrepareInstallation(ev, transaction, leaseState);
            if (preparation.Notification is { } notification)
                yield return notification;
            if (preparation.Prepared is not { } prepared)
                yield break;
            if (ev.CancellationToken.IsCancellationRequested)
            {
                prepared.Abort();
                yield break;
            }
            try { transaction.Commit(); }
            catch (Exception error)
            {
                try { prepared.Abort(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Ability installation commit and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            Guid operationId = ev.CommittedOperationId ?? Guid.NewGuid();
            if (!ev.CommittedOperationId.HasValue)
                ev.Context.MarkCurrentOperationCommitted(operationId);
            foreach (Event published in PublishInstallation(ev, prepared.Publication, operationId))
                yield return published;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnStatusApplicationBatchReady(StatusApplicationBatchReadyEvent ev)
        {
            StatusReleaseBatch batch = ev.Batch ??
                throw new InvalidOperationException("The status batch is missing.");
            if (batch.IsAborted) yield break;
            if (batch.IsCommitted)
                throw new InvalidOperationException("The status release has already committed.");
            UnitActionGateRequestEvent request = batch.Release.Request;
            AbilityTriggerInvokeEvent trigger = batch.Trigger;
            CommandGateManager gate = request.AbilityGate ??
                throw new InvalidOperationException("A coordinated status release requires its gate.");
            Unit source = request.Source;
            if (!ReferenceEquals(ev.Receiver, source.EventReceiver) ||
                !ReferenceEquals(source.EventReceiver.Context, ev.Context))
                throw new InvalidOperationException("The status batch must return to its source.");
            if (trigger.CancellationToken.IsCancellationRequested || request.ActionCancellation.IsCancellationRequested)
            {
                batch.Reject();
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            CommandExecutionFailureReason failure;
            IReadOnlyList<Unit> targets;
            try
            {
                failure = StatusTargeting.Prepare(
                    trigger, batch.Ability.Binding, batch.Ability.Selector, out targets);
            }
            catch (Exception error)
            {
                try { batch.Abort(ev.Context); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Status target validation and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            if (failure != CommandExecutionFailureReason.None)
            {
                batch.Reject();
                yield return TriggeredAbilityGate.Result(batch.Ability, trigger, request.ActivationId,
                    CommandExecutionStatus.Failure, failure);
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            if (!batch.Matches(targets))
            {
                AbilityInstallRequestEvent[] requests;
                try
                {
                    if (targets.Any(target => target.EventReceiver.GetHandlers<AbilityInstallHandler>().Count != 1))
                        throw new InvalidOperationException("A coordinated status release requires one installer on each target.");
                    batch.BeginRound(targets);
                    requests = StatusApplicationRequests.Create(batch.Ability, trigger, targets,
                        request.AbilityTarget ??
                            throw new InvalidOperationException("A gated status release requires its captured ability."),
                        batch).ToArray();
                }
                catch (Exception error)
                {
                    try { batch.Abort(ev.Context); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("Status retry preparation and cleanup failed.", error, cleanupError);
                    }
                    throw;
                }
                try
                {
                    foreach (Event next in requests)
                        foreach (IEvent _ in ev.Context.Dispatch(next)) { }
                }
                catch (Exception error)
                {
                    try { batch.Abort(ev.Context); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("Status request delivery and cleanup failed.", error, cleanupError);
                    }
                    throw;
                }
                yield break;
            }

            var prepared = new List<PreparedStatusTarget>();
            PeriodicEffectRegistry.PreparedChanges? periodic = null;
            PeriodicEffectRejectionReason rejection = PeriodicEffectRejectionReason.None;
            string? rejectedAbilityId = null;
            bool installationCanceled = false;
            CommandReleaseResult result;
            try
            {
                result = gate.CommitRelease(source, request.ActivationId, ev.Context.ContextTimeSpan,
                    request.ActionCancellation.Token, trigger.CancellationToken, ev, transaction =>
                    {
                        foreach (AbilityInstallEvent installation in batch.Installations)
                        {
                            InstallationPreparation preparation = PrepareInstallation(
                                installation, transaction, installation.Target.Abilities.Leases, deferPeriodic: true);
                            prepared.Add(new PreparedStatusTarget(installation, preparation));
                            if (preparation.Prepared == null && preparation.Notification == null)
                            {
                                installationCanceled = true;
                                return false;
                            }
                        }
                        PeriodicEffectRegistry.StatusChange[] changes = prepared
                            .Select(entry => entry.Preparation.PendingPeriodic)
                            .OfType<PeriodicEffectRegistry.StatusChange>().ToArray();
                        if (changes.Length == 0) return true;
                        periodic = trigger.TriggerChain.PeriodicEffects.PrepareStatusBatch(
                            transaction, trigger.TriggerChain, source, changes, default,
                            out rejection, out rejectedAbilityId);
                        return periodic != null;
                    });
            }
            catch (Exception error)
            {
                try
                {
                    if (gate.WasCommitted(request.ActivationId))
                    {
                        batch.MarkCommitted();
                        PublishAndDispatchStatusBatch(ev, batch, prepared, periodic);
                    }
                    else
                    {
                        var failures = new List<Exception>();
                        try { AbortStatusBatch(prepared, periodic); }
                        catch (Exception cleanupError) { failures.Add(cleanupError); }
                        try { batch.Abort(ev.Context); }
                        catch (Exception cleanupError) { failures.Add(cleanupError); }
                        if (failures.Count > 0) throw new AggregateException(failures);
                    }
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("The status release and its cleanup failed.", error, cleanupError);
                }
                throw;
            }

            if (result != CommandReleaseResult.Committed)
            {
                try { AbortStatusBatch(prepared, periodic); }
                catch (Exception error)
                {
                    try { batch.Abort(ev.Context); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("Status rejection cleanup failed.", error, cleanupError);
                    }
                    throw;
                }
                batch.Reject();
                if (result == CommandReleaseResult.EffectRejected && !installationCanceled &&
                    rejection != PeriodicEffectRejectionReason.Canceled)
                {
                    PreparedStatusTarget rejected = prepared.First(entry =>
                        entry.Preparation.PendingPeriodic?.Additions.Any(effect =>
                            string.Equals(effect.EffectId, rejectedAbilityId, StringComparison.Ordinal)) == true);
                    yield return ev.Context.Factory.NewEvent<StatusApplicationRejectedEvent>() with
                    {
                        CommandEventId = trigger.CommandEventId, Source = source,
                        Target = rejected.Installation.Target,
                        StatusId = batch.PayloadId ??
                            throw new InvalidOperationException("The status has no definition."),
                        PeriodicEffectId = rejectedAbilityId ??
                            throw new InvalidOperationException("The rejected effect has no ID."),
                        Reason = rejection,
                        Receiver = rejected.Installation.CompletionReceiver ?? rejected.Installation.Target.EventReceiver,
                        IsScopeDetached = true
                    };
                    yield return TriggeredAbilityGate.Result(batch.Ability, trigger, request.ActivationId,
                        CommandExecutionStatus.Failure, CommandExecutionFailureReason.PeriodicEffectLimit);
                }
                else if (result is not CommandReleaseResult.Canceled and not CommandReleaseResult.EffectRejected)
                    yield return TriggeredAbilityGate.Result(batch.Ability, trigger, request.ActivationId,
                        CommandExecutionStatus.Failure, TriggeredAbilityGate.Failure(result));
                yield return TriggeredAbilityGate.Finish(ev, request);
                yield break;
            }

            batch.MarkCommitted();
            PublishAndDispatchStatusBatch(ev, batch, prepared, periodic);
        }

        private static void PublishAndDispatchStatusBatch(
            Event ev, StatusReleaseBatch batch, IReadOnlyList<PreparedStatusTarget> prepared,
            PeriodicEffectRegistry.PreparedChanges? periodic)
        {
            UnitActionGateRequestEvent request = batch.Release.Request;
            CommandGateManager gate = request.AbilityGate ??
                throw new InvalidOperationException("A coordinated status release requires its gate.");
            IReadOnlyList<Event> notifications = PublishStatusBatch(
                ev, batch, prepared, periodic, request.ActivationId, out List<Exception> failures);
            var committedEvents = new List<Event>();
            if (gate.ResourceCost > 0)
                committedEvents.Add(UnitEnergyChangedEvent.FromUnit(ev.Context, request.Source)
                    with { CommittedOperationId = request.ActivationId });
            committedEvents.Add(TriggeredAbilityGate.Result(batch.Ability, batch.Trigger, request.ActivationId,
                CommandExecutionStatus.Success) with { CommittedOperationId = request.ActivationId });
            committedEvents.AddRange(notifications.Where(notification =>
                gate.ResourceCost is not > 0 || notification is not UnitEnergyChangedEvent energy ||
                !ReferenceEquals(energy.Unit, request.Source)));
            committedEvents.AddRange(TriggeredAbilityGate.FinishAfterRelease(ev, request));
            CommittedEventPublisher.Dispatch(ev.Context, committedEvents, failures);
        }

        private static void AbortStatusBatch(
            IReadOnlyList<PreparedStatusTarget> prepared, PeriodicEffectRegistry.PreparedChanges? periodic)
        {
            var failures = new List<Exception>();
            if (periodic != null)
                try { periodic.Abort(); } catch (Exception error) { failures.Add(error); }
            foreach (PreparedStatusTarget entry in prepared)
                if (entry.Preparation.Prepared is { } installation)
                    try { installation.Abort(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0)
                throw new AggregateException("Prepared status installations failed to close.", failures);
        }

        private static IReadOnlyList<Event> PublishStatusBatch(
            Event ev, StatusReleaseBatch batch, IReadOnlyList<PreparedStatusTarget> prepared,
            PeriodicEffectRegistry.PreparedChanges? periodic, Guid operationId, out List<Exception> failures)
        {
            var notifications = new List<Event>();
            failures = [];
            if (periodic != null)
            {
                periodic.Publish();
                failures.AddRange(periodic.CloseRetired());
            }
            foreach (PreparedStatusTarget entry in prepared)
            {
                if (entry.Preparation.Notification is { } notification)
                    notifications.Add(notification with { CommittedOperationId = operationId });
                if (entry.Preparation.Prepared is { } installation)
                {
                    try { notifications.AddRange(PublishInstallation(entry.Installation, installation.Publication, operationId)); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            if (periodic != null)
                foreach (PeriodicEffectRegistry.PreparedEffect effect in periodic.Added)
                {
                    AbilityInstallEvent installation = prepared.Single(entry =>
                        ReferenceEquals(entry.Installation.Target, effect.Handle.Target)).Installation;
                    try
                    {
                        notifications.AddRange(PeriodicEffectRegistry.CreateStartEvents(
                            ev.Context, batch.Trigger.CommandEventId, batch.Release.Request.Source,
                            effect.Handle.Target, installation.CompletionReceiver, effect.Application,
                            effect.Handle, operationId));
                    }
                    catch (Exception error) { failures.Add(error); }
                }
            return notifications;
        }

        private static InstallationPreparation PrepareInstallation(
            AbilityInstallEvent ev, AtomicTransaction transaction, LeaseState leaseState, bool deferPeriodic = false)
        {
            if (ev.Handlers.OfType<IAbilityRuntimePart>().Any(part => part.Binding.Source is AbilitySourceInstance))
                throw new InvalidOperationException("Managed source parts must be installed through a source change event.");
            if (ev.Purpose == AbilityInstallPurpose.Definition)
                RejectManagedDefinitionChanges(ev.Target, ev.Handlers.Select(handler => handler.Id));
            Dictionary<string, Guid> activeLeases = leaseState.Active;
            Dictionary<string, RealTimeLease> realTimeLeases = leaseState.RealTime;
            Dictionary<Guid, SimulationLease> simulationLeases = leaseState.Simulation;
            if (!Enum.IsDefined(ev.Purpose))
                throw new ArgumentOutOfRangeException(nameof(ev.Purpose));
            if (!Enum.IsDefined(ev.LeaseCancellationPolicy))
                throw new ArgumentOutOfRangeException(nameof(ev.LeaseCancellationPolicy));
            ArgumentNullException.ThrowIfNull(ev.PeriodicEffects);
            bool isStatusApplication = ev.Purpose == AbilityInstallPurpose.StatusApplication;
            if (isStatusApplication)
            {
                if (ev.StatusBatch == null)
                    ValidateStatusApplication(ev.Context, ev.Source, ev.Target, ev.ApplicationOrigin, ev);
                else
                {
                    if (!ev.Context.ContainsReceiver(ev.Target.EventReceiver) ||
                        ev.Source == null || !ev.Context.ContainsReceiver(ev.Source.EventReceiver))
                        throw new InvalidOperationException("Status participants must belong to this context.");
                    (ev.ApplicationOrigin ?? throw new InvalidOperationException("A status application requires a real origin."))
                        .ValidatePreparedInstall(ev.Context, ev);
                }
                if (ev.Handlers.Count != 1 || ev.Handlers[0] is not StatusAbility { IsTriggerListener: false })
                    throw new InvalidOperationException("A status application requires one materialized status.");
            }
            else if (ev.ApplicationOrigin != null || ev.PeriodicEffects.Count > 0 ||
                ev.Handlers.OfType<StatusAbility>().Any(status =>
                !status.IsTriggerListener && status.Labels.HasFlag(AbilityLabel.Status)))
                throw new InvalidOperationException("Applied statuses require a real application origin.");
            bool committedLeaseIsIndependent =
                ev.LeaseCancellationPolicy == AbilityLeaseCancellationPolicy.ContinueAfterCommit;
            UnitProperty before = ev.Target.Property;
            decimal previousEnergy = before.Energy;
            int previousEnergyMax = before.EnergyMax;
            List<IHandler> toAdd = new List<IHandler>();
            HashSet<string> retainedLeaseIds = new(StringComparer.Ordinal);
            var applicationUpdates = new Dictionary<StatusAbility, (int Stack, Unit? Source, StatusApplicationOrigin? Origin)>(
                ReferenceEqualityComparer.Instance);
            var applications = new Dictionary<StatusAbility, (string PayloadId, Guid InstanceId, string Id)>(
                ReferenceEqualityComparer.Instance);
            var replacedStatuses = new HashSet<IHandler>(ReferenceEqualityComparer.Instance);
            var replacedStatusIds = new HashSet<string>(StringComparer.Ordinal);
            var labelUpdates = new Dictionary<AbilityBase, AbilityLabel>(ReferenceEqualityComparer.Instance);
            List<string> installedIds = [];
            Guid? currentStatusApplicationId = null;
            Guid? replacedStatusApplicationId = null;
            string? statusDefinitionId = null;
            IReadOnlyList<PeriodicEffectApplication> statusPeriodicEffects = ev.PeriodicEffects;

            string PlannedId(IHandler handler) =>
                handler is StatusAbility status && applications.TryGetValue(status, out var application)
                    ? application.Id : handler.Id;

            if (ev.ExpireAt.HasValue && ev.Handlers.OfType<StatusAbility>()
                .Any(status => !status.IsTriggerListener && status.TimeBasis == AbilityTimeBasis.RealTime))
            {
                throw new InvalidOperationException("A real-time ability lease cannot use a simulation expiry.");
            }
            if (ev.Handlers.OfType<StatusAbility>().Any(status =>
                !Enum.IsDefined(status.TimeBasis) ||
                !status.IsTriggerListener && status.TimeBasis == AbilityTimeBasis.RealTime &&
                (status.StackPolicy != AbilityStackPolicy.Independent || status.Duration <= TimeSpan.Zero) ||
                status.ProtectedUntilExpiry && status.TimeBasis != AbilityTimeBasis.RealTime))
            {
                throw new InvalidOperationException("A protected status requires an independent real-time lease with positive duration.");
            }
            if (!committedLeaseIsIndependent && ev.CancellationTokenSource != null &&
                ev.Handlers.OfType<StatusAbility>().Any(status => status.ProtectedUntilExpiry))
                throw new InvalidOperationException("A protected status cannot follow request cancellation after commit.");

            HashSet<string> protectedIds = ProtectedStatusIds(ev.Target);
            IReadOnlyList<IHandler> mountedHandlers = ev.Target.EventReceiver.GetHandlers<IHandler>();
            foreach (IHandler handler in ev.Handlers)
            {
                if (IsProtectedPreset(handler))
                    throw new InvalidOperationException("A preset ability handler cannot be installed.");
                if (ev.Context.FindHandlerReceivers(handler).Any(receiver =>
                    !ReferenceEquals(receiver, ev.Target.EventReceiver)))
                    throw new InvalidOperationException("An instance is still registered on another unit.");
                if (isStatusApplication && handler is StatusAbility applied)
                {
                    if (!Enum.IsDefined(applied.StackPolicy))
                        throw new InvalidOperationException("The status policy or level is invalid.");
                    if (mountedHandlers.Any(mounted => ReferenceEquals(mounted, applied)))
                        throw new InvalidOperationException("A status application requires a fresh instance.");
                    string payloadId = applied.PayloadId ?? applied.Id;
                    statusDefinitionId = payloadId;
                    StatusAbility? existing = applied.StackPolicy == AbilityStackPolicy.Independent ? null :
                        mountedHandlers.OfType<StatusAbility>().SingleOrDefault(status =>
                            !status.IsTriggerListener && status.StackPolicy != AbilityStackPolicy.Independent &&
                            string.Equals(status.PayloadId, payloadId, StringComparison.Ordinal) &&
                            status.ApplicationOrigin != null &&
                            status.ApplicationOrigin.IsSameInstance(ev.ApplicationOrigin!) &&
                            ReferenceEquals(status.ApplicationSource, ev.Source));
                    if (existing != null)
                    {
                        if (existing.StackPolicy != applied.StackPolicy)
                            throw new InvalidOperationException("A status definition cannot change its stacking policy.");
                        int incomingLevel = applied.Level;
                        int existingLevel = existing.Level;
                        if (incomingLevel < existingLevel)
                        {
                            Event ignored = ev.Context.Factory.NewEvent<StatusApplicationIgnoredEvent>() with
                            {
                                CommandEventId = ev.CommandEventId, Source = ev.Source, Target = ev.Target,
                                StatusId = payloadId, AttemptedLevel = incomingLevel, ExistingLevel = existingLevel,
                                Receiver = ev.CompletionReceiver ?? ev.Target.EventReceiver, IsScopeDetached = true
                            };
                            return new InstallationPreparation(null, ignored);
                        }
                        if (incomingLevel == existingLevel)
                        {
                            statusPeriodicEffects = [];
                            currentStatusApplicationId = existing.ApplicationId ??
                                throw new InvalidOperationException("The existing status has no application identity.");
                            int stack = existing.CurrentStack;
                            if (existing.StackPolicy is AbilityStackPolicy.StackOnly or AbilityStackPolicy.ExtendAndStack &&
                                (!existing.Stack.HasValue || stack < existing.Stack.Value))
                                stack = checked(stack + 1);
                            if (existing.StackPolicy == AbilityStackPolicy.StackOnly)
                                retainedLeaseIds.Add(existing.Id);
                            applicationUpdates.Add(existing, (stack, existing.ApplicationSource, existing.ApplicationOrigin));
                            installedIds.Add(existing.Id);
                            continue;
                        }
                        if (protectedIds.Contains(existing.Id))
                            throw new InvalidOperationException("A protected status application cannot be replaced.");
                        replacedStatuses.Add(existing);
                        replacedStatusIds.Add(existing.Id);
                        replacedStatusApplicationId = existing.ApplicationId ??
                            throw new InvalidOperationException("The replaced status has no application identity.");
                    }

                    Guid applicationId = Guid.NewGuid();
                    currentStatusApplicationId = applicationId;
                    applications.Add(applied, (payloadId, applicationId, $"{payloadId}#{applicationId:N}"));
                    applicationUpdates.Add(applied, (applied.CurrentStack, ev.Source, ev.ApplicationOrigin));
                    installedIds.Add(PlannedId(applied));
                    toAdd.Add(applied);
                    continue;
                }
                if (handler is StatusAbility
                    { StackPolicy: AbilityStackPolicy.Independent, IsTriggerListener: false } independent)
                {
                    if (mountedHandlers.Any(mounted => ReferenceEquals(mounted, independent)))
                        throw new InvalidOperationException("An independent status application requires a fresh instance.");
                    string payloadId = independent.PayloadId ?? independent.Id;
                    Guid applicationId = Guid.NewGuid();
                    applications.Add(independent,
                        (payloadId, applicationId, $"{payloadId}#{applicationId:N}"));
                }

                installedIds.Add(PlannedId(handler));
                if (handler is AbilityBase incomingAbility)
                {
                    AbilityBase? mounted = mountedHandlers.OfType<AbilityBase>()
                        .FirstOrDefault(ability => string.Equals(ability.Id, PlannedId(handler), StringComparison.Ordinal));
                    if (mounted != null)
                    {
                        labelUpdates[incomingAbility] = incomingAbility.Labels | (mounted.Labels &
                            (AbilityLabel.FromCharacter | AbilityLabel.FromEquipment | AbilityLabel.FromTalent));
                    }
                }
                if (handler is StatusAbility incomingStatus)
                {
                    StatusAbility? existing = mountedHandlers.OfType<StatusAbility>()
                        .FirstOrDefault(m => string.Equals(m.Id, PlannedId(incomingStatus), StringComparison.Ordinal));

                    if (existing != null)
                    {
                        (int stack, Unit? source, StatusApplicationOrigin? origin) =
                            applicationUpdates.TryGetValue(existing, out var update)
                                ? update : (existing.CurrentStack, existing.ApplicationSource, existing.ApplicationOrigin);
                        switch (existing.StackPolicy)
                        {
                            case AbilityStackPolicy.StackOnly:
                                if (!existing.Stack.HasValue || stack < existing.Stack.Value)
                                    stack = checked(stack + 1);
                                retainedLeaseIds.Add(existing.Id);
                                break;

                            case AbilityStackPolicy.ExtendOnly:
                                if (!existing.IsTriggerListener)
                                    source = ev.Source;
                                break;

                            case AbilityStackPolicy.ExtendAndStack:
                                if (!existing.IsTriggerListener)
                                    source = ev.Source;
                                if (!existing.Stack.HasValue || stack < existing.Stack.Value)
                                    stack = checked(stack + 1);
                                break;
                            case AbilityStackPolicy.Independent:
                                throw new InvalidOperationException("Independent status applications must have unique IDs.");
                            default:
                                throw new ArgumentOutOfRangeException(nameof(existing.StackPolicy));
                        }

                        applicationUpdates[existing] = (stack, source, origin);
                        continue;
                    }
                    if (!incomingStatus.IsTriggerListener)
                        applicationUpdates[incomingStatus] = (incomingStatus.CurrentStack, ev.Source, null);
                }

                toAdd.Add(handler);
            }

            string[] refreshedIds = installedIds
                .Where(id => !string.Equals(id, nameof(AbilityInstallHandler), StringComparison.Ordinal)
                    && !retainedLeaseIds.Any(retained => AbilityIdMatcher.IsSameOrChild(retained, id)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            HashSet<string> realTimeIds = ev.Handlers.OfType<StatusAbility>()
                .Where(status => !status.IsTriggerListener && status.TimeBasis == AbilityTimeBasis.RealTime)
                .Select(PlannedId).ToHashSet(StringComparer.Ordinal);
            string[] simulationIds = refreshedIds.Where(id => !realTimeIds.Contains(id)).ToArray();
            AbilityLeaseReference[] invalidated = activeLeases
                .Where(pair => (replacedStatusIds.Contains(pair.Key) ||
                    refreshedIds.Any(id => AbilityIdMatcher.IsSameOrChild(id, pair.Key)))
                    && !protectedIds.Contains(pair.Key)
                    && !retainedLeaseIds.Any(retained => AbilityIdMatcher.IsSameOrChild(retained, pair.Key)))
                .Select(pair => new AbilityLeaseReference(pair.Key, pair.Value))
                .ToArray();

            foreach ((StatusAbility status, (int stack, Unit? source, StatusApplicationOrigin? origin)) in applicationUpdates)
                status.StageApplication(transaction, stack, source, origin);
            IReadOnlyList<IHandler> proposed = mountedHandlers;
            IHandler[] removedHandlers = [];
            if (toAdd.Count > 0)
            {
                ValidateChargeGates(ev.Context, toAdd);
                HashSet<string> idSet = toAdd.Select(PlannedId).ToHashSet(StringComparer.Ordinal);
                if (idSet.Overlaps(protectedIds))
                    throw new InvalidOperationException("An installed ability cannot replace a protected status application.");
                removedHandlers = mountedHandlers.Where(m => replacedStatuses.Contains(m) ||
                    !protectedIds.Contains(m.Id) && idSet.Any(id => AbilityIdMatcher.IsSameOrChild(id, m.Id))).ToArray();
                if (removedHandlers.Any(IsProtectedPreset))
                    throw new InvalidOperationException("A preset ability handler cannot be replaced.");
                foreach (IAbilityRuntimePart part in toAdd.OfType<IAbilityRuntimePart>())
                    part.Binding.StageContext(transaction, ev.Context);
                proposed = ev.Target.EventReceiver.StageHandlerChanges(transaction, removedHandlers, toAdd);
                if (ev.Causality is AbilityTriggerChain triggerChain)
                    triggerChain.Router.StageChanges(transaction,
                        [new TriggerRoutingChange(ev.Target, removedHandlers, toAdd)]);
            }
            ev.Target.Abilities.StageReApply(transaction, proposed);
            StageLeaseScopeCancellations(transaction, leaseState, invalidated);

            bool hasLease = simulationIds.Length > 0 &&
                (ev.ExpireAt.HasValue && ev.ExpireAt.Value < TimeSpan.MaxValue ||
                 !committedLeaseIsIndependent && ev.CancellationTokenSource != null);
            Guid instanceId = hasLease ? Guid.NewGuid() : Guid.Empty;
            CancellationTokenSource? leaseToken = null;
            CancellationTokenSource? ownedLeaseToken = null;
            EventScope? leaseScope = null;
            SimulationLease? simulation = null;
            var preparedRealTime = new List<PreparedRealTimeLease>();
            var preparedScopes = new List<EventScope>();
            var preparedTimers = new List<ITimer>();
            PeriodicEffectRegistry.PreparedChanges? periodicChanges = null;
            PeriodicEffectRegistry.StatusChange? pendingPeriodic = null;
            PeriodicEffectRejectionReason periodicRejection = PeriodicEffectRejectionReason.None;
            string? rejectedAbilityId = null;
            bool canceledBeforeCommit = false;
            try
            {
                foreach (StatusAbility status in toAdd.OfType<StatusAbility>()
                    .Where(status => !status.IsTriggerListener && status.TimeBasis == AbilityTimeBasis.RealTime))
                {
                    if (status.StackPolicy != AbilityStackPolicy.Independent ||
                        !applications.TryGetValue(status, out var application) || status.Duration <= TimeSpan.Zero)
                        throw new InvalidOperationException("A real-time status requires an independent application and positive duration.");
                    if (realTimeLeases.ContainsKey(application.Id))
                        throw new InvalidOperationException("The real-time status application is already active.");
                    TimeProvider clock = ev.Context.TimeProvider;
                    long startedAt = clock.GetTimestamp();
                    EventScope scope = ev.Context.CreateEventScope();
                    preparedScopes.Add(scope);
                    Unit target = ev.Target;
                    ContextEventInbox inbox = ev.Context.Inbox;
                    TimeSpan remaining = status.Duration - clock.GetElapsedTime(startedAt);
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                    ITimer timer = clock.CreateTimer(_ => inbox.TryPost(new AbilityRealtimeLeaseRecheckEvent
                    {
                        Target = target, Receiver = target.EventReceiver, TimeSpan = TimeSpan.Zero
                    }), null, remaining, Timeout.InfiniteTimeSpan);
                    preparedTimers.Add(timer);
                    CancellationToken requestToken = !committedLeaseIsIndependent && ev.CancellationTokenSource != null
                        ? ev.CancellationTokenSource.Token : default;
                    var lease = new RealTimeLease(ev.Source, target, ev.CommandEventId, application.Id,
                        application.InstanceId, startedAt, status.Duration, timer, scope, requestToken);
                    preparedRealTime.Add(new PreparedRealTimeLease(status, clock, lease));
                    if (requestToken.CanBeCanceled)
                        lease.CancellationRegistration = requestToken.Register(() =>
                            inbox.TryPost(new AbilityRealtimeLeaseRecheckEvent
                            {
                                Target = target, Receiver = target.EventReceiver, TimeSpan = TimeSpan.Zero
                            }));
                }

                if (hasLease)
                {
                    if (simulationLeases.ContainsKey(instanceId))
                        throw new InvalidOperationException("The simulation lease is already active.");
                    leaseToken = committedLeaseIsIndependent || ev.CancellationTokenSource == null
                        ? ownedLeaseToken = new CancellationTokenSource()
                        : ev.CancellationTokenSource;
                    leaseScope = ev.Context.CreateEventScope();
                    preparedScopes.Add(leaseScope);
                    CancellationToken requestToken = !committedLeaseIsIndependent && ev.CancellationTokenSource != null
                        ? ev.CancellationTokenSource.Token : default;
                    simulation = new SimulationLease(leaseScope, simulationIds.ToHashSet(StringComparer.Ordinal),
                        requestToken, ev.Source, ev.CommandEventId, ev.ExpireAt);
                    if (requestToken.CanBeCanceled)
                    {
                        ContextEventInbox inbox = ev.Context.Inbox;
                        Unit target = ev.Target;
                        simulation.CancellationRegistration = requestToken.Register(() =>
                            inbox.TryPost(new AbilitySimulationLeaseCanceledEvent
                            {
                                Target = target, InstanceId = instanceId,
                                Receiver = target.EventReceiver, TimeSpan = TimeSpan.Zero
                            }));
                    }
                }
                if (isStatusApplication && (statusPeriodicEffects.Count > 0 || replacedStatusApplicationId.HasValue))
                {
                    if (ev.Causality is AbilityTriggerChain chain)
                    {
                        if (deferPeriodic)
                        {
                            if (!committedLeaseIsIndependent)
                                throw new InvalidOperationException("A coordinated status requires an independent committed lease.");
                            pendingPeriodic = new PeriodicEffectRegistry.StatusChange(
                                ev.Target, statusPeriodicEffects,
                                currentStatusApplicationId ??
                                    throw new InvalidOperationException("The status has no application identity."),
                                replacedStatusApplicationId, ev.ApplicationOrigin ??
                                    throw new InvalidOperationException("The status has no application origin."));
                        }
                        else
                        {
                            HashSet<Guid> removed = replacedStatusApplicationId is { } replaced
                                ? [replaced] : [];
                            HashSet<Guid> replaceable = replacedStatusApplicationId is { } old
                                ? [old] : currentStatusApplicationId is { } current ? [current] : [];
                            EventCancellation cancellation = committedLeaseIsIndependent
                                ? default(CancellationToken) : ev.CancellationToken;
                            periodicChanges = chain.PeriodicEffects.PrepareChanges(
                                transaction, chain, ev.Source, ev.Target, statusPeriodicEffects, removed,
                                currentStatusApplicationId, replaceable, ev.ApplicationOrigin, cancellation,
                                out periodicRejection, out rejectedAbilityId);
                        }
                    }
                    else if (statusPeriodicEffects.Count > 0)
                        throw new InvalidOperationException("Status periodic effects require a TriggerChainFactory.");
                }
                canceledBeforeCommit = ev.CancellationToken.IsCancellationRequested;
            }
            catch (Exception error)
            {
                try { CleanupPreparedInstall(preparedScopes, preparedTimers, preparedRealTime, simulation, ownedLeaseToken, periodicChanges); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Ability installation preparation and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            if (canceledBeforeCommit || periodicRejection != PeriodicEffectRejectionReason.None)
            {
                CleanupPreparedInstall(preparedScopes, preparedTimers, preparedRealTime, simulation, ownedLeaseToken, periodicChanges);
                Event? notification = null;
                if (!canceledBeforeCommit && periodicRejection != PeriodicEffectRejectionReason.Canceled)
                    notification = ev.Context.Factory.NewEvent<StatusApplicationRejectedEvent>() with
                    {
                        CommandEventId = ev.CommandEventId, Source = ev.Source, Target = ev.Target,
                        StatusId = statusDefinitionId ?? throw new InvalidOperationException("The status has no definition."),
                        PeriodicEffectId = rejectedAbilityId ?? throw new InvalidOperationException("The rejected effect has no ID."),
                        Reason = periodicRejection, Receiver = ev.CompletionReceiver ?? ev.Target.EventReceiver,
                        IsScopeDetached = true
                    };
                return new InstallationPreparation(null, notification);
            }

            var publication = new InstallationPublication(
                leaseState, committedLeaseIsIndependent, before, previousEnergy, previousEnergyMax,
                toAdd, removedHandlers, labelUpdates, applications, invalidated, preparedRealTime,
                simulation, instanceId, simulationIds, hasLease, leaseToken, leaseScope, installedIds,
                periodicChanges);
            return new InstallationPreparation(
                new PreparedInstallation(publication, preparedScopes, preparedTimers, ownedLeaseToken),
                null, pendingPeriodic);
        }

        private static IEnumerable<Event> PublishInstallation(
            AbilityInstallEvent ev, InstallationPublication publication, Guid operationId)
        {
            var (leaseState, committedLeaseIsIndependent, before, previousEnergy, previousEnergyMax,
                toAdd, removedHandlers, labelUpdates, applications, invalidated, preparedRealTime,
                simulation, instanceId, simulationIds, hasLease, leaseToken, leaseScope, installedIds,
                periodicChanges) = publication;
            Dictionary<string, Guid> activeLeases = leaseState.Active;
            Dictionary<string, RealTimeLease> realTimeLeases = leaseState.RealTime;
            Dictionary<Guid, SimulationLease> simulationLeases = leaseState.Simulation;
            DetachChargeGates(removedHandlers.Where(handler =>
                !toAdd.Any(added => ReferenceEquals(added, handler))));
            EnterChargeGates(ev.Context, ev.Target, toAdd);
            periodicChanges?.Publish();
            foreach ((AbilityBase ability, AbilityLabel labels) in labelUpdates)
                ability.Labels = labels;
            foreach ((StatusAbility status, (string payloadId, Guid applicationId, string id)) in applications)
            {
                status.PayloadId = payloadId;
                status.ApplicationId = applicationId;
                status.Id = id;
            }
            RetiredLeases retired = DetachLeaseRecords(leaseState, invalidated);
            foreach (PreparedRealTimeLease prepared in preparedRealTime)
            {
                RealTimeLease lease = prepared.Lease;
                prepared.Status.StartRealTimeLease(prepared.Clock, lease.StartedAt);
                realTimeLeases.Add(lease.AbilityId, lease);
                activeLeases[lease.AbilityId] = lease.InstanceId;
            }
            if (simulation != null)
            {
                simulationLeases.Add(instanceId, simulation);
                foreach (string id in simulationIds) activeLeases[id] = instanceId;
            }
            var cleanupFailures = new List<Exception>();
            if (periodicChanges != null)
                cleanupFailures.AddRange(periodicChanges.CloseRetired());
            cleanupFailures.AddRange(CleanupRetiredLeases(retired));
            var removal = new LeaseRemovalResult(retired.Completed, cleanupFailures);
            ev.Target.Abilities.PublishProjection();
            if (ev.Target.Property.LifePoint is int life &&
                (life != before.LifePoint || ev.Target.Property.LifePointMax != before.LifePointMax))
                yield return UnitLifeChangedEvent.FromUnit(ev.Context, ev.Target, before.LifePoint ?? life)
                    with { IsScopeDetached = true, CommittedOperationId = operationId };
            if (ev.Target.Property.Energy != previousEnergy || ev.Target.Property.EnergyMax != previousEnergyMax)
                yield return UnitEnergyChangedEvent.FromUnit(ev.Context, ev.Target)
                    with { IsScopeDetached = true, CommittedOperationId = operationId };

            foreach (PreparedRealTimeLease prepared in preparedRealTime)
            {
                RealTimeLease lease = prepared.Lease;
                yield return ev.Context.Factory.NewEvent<AbilityLeaseActivatedEvent>() with
                {
                    Target = lease.Target, AbilityIds = [lease.AbilityId], InstanceId = lease.InstanceId,
                    ExpiresAt = TimeSpan.MaxValue, Receiver = lease.Target.EventReceiver,
                    TimeSpan = TimeSpan.Zero, Priority = LeaseNotificationPriority,
                    LeaseCancellation = lease.Scope.Token, IsScopeDetached = true, CommittedOperationId = operationId
                };
            }

            if (invalidated.Length > 0)
            {
                yield return CreateInvalidationEvent(ev, ev.Target, invalidated, removal.Completed)
                    with { IsScopeDetached = true, CommittedOperationId = operationId };
            }
            if (hasLease)
            {
                yield return ev.Context.Factory.NewEvent<AbilityLeaseActivatedEvent>() with
                {
                    Target = ev.Target,
                    AbilityIds = simulationIds,
                    InstanceId = instanceId,
                    ExpiresAt = ev.ExpireAt ?? TimeSpan.MaxValue,
                    Receiver = ev.Target.EventReceiver,
                    TimeSpan = TimeSpan.Zero,
                    Priority = LeaseNotificationPriority,
                    CancellationTokenSource = leaseToken,
                    LeaseCancellation = leaseScope!.With(leaseToken!.Token),
                    IsScopeDetached = true,
                    CommittedOperationId = operationId,
                };
            }

            AbilityInstalledEvent installed = ev.Context.Factory.NewEvent<AbilityInstalledEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = ev.Target,
                AbilityIds = installedIds,
                ProjectionAlreadyApplied = true,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = committedLeaseIsIndependent ? null : ev.CancellationTokenSource,
                IsScopeDetached = true,
                CommittedOperationId = operationId,
            };
            yield return installed;
            if (ev.CompletionReceiver != null && ev.CompletionReceiver != ev.Receiver)
                yield return installed with { Receiver = ev.CompletionReceiver };

            if (periodicChanges != null)
                foreach (PeriodicEffectRegistry.PreparedEffect effect in periodicChanges.Added)
                    foreach (Event next in PeriodicEffectRegistry.CreateStartEvents(
                        ev.Context, ev.CommandEventId, ev.Source, ev.Target, ev.CompletionReceiver,
                        effect.Application, effect.Handle, operationId))
                        yield return next;

            if (!hasLease)
            {
                ThrowLeaseCleanupFailures(removal.Failures);
                yield break;
            }

            string leaseId = $"install:{string.Concat(simulationIds.Select(id => $"{id.Length}:{id}"))}";
            yield return ev.Context.Factory.NewEvent<AbilityLifecycleEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = ev.Target,
                Receiver = ev.Target.EventReceiver,
                Stage = AbilityLifecycleStage.Start,
                LeaseId = leaseId,
                LeaseInstanceId = instanceId,
                LeaseAbilityIds = simulationIds,
                LeaseExpireAt = ev.ExpireAt,
                RemainingTicks = 0,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = leaseToken,
                Scope = leaseScope,
            };
            ThrowLeaseCleanupFailures(removal.Failures);
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> RecheckRealTimeLeases(AbilityRealtimeLeaseRecheckEvent ev)
        {
            if (ev.Receiver != ev.Target.EventReceiver) yield break;
            foreach (RealTimeLease lease in realTimeLeases.Values)
            {
                if (!lease.RequestToken.IsCancellationRequested &&
                    ev.Context.TimeProvider.GetElapsedTime(lease.StartedAt) < lease.Duration) continue;
                yield return ev.Context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
                {
                    CommandEventId = lease.CommandId, Source = lease.Source, Target = lease.Target,
                    AbilityIds = [lease.AbilityId], LeaseInstanceId = lease.InstanceId,
                    Receiver = lease.Target.EventReceiver, TimeSpan = TimeSpan.Zero
                };
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> RecheckCanceledSimulationLeases(AbilitySimulationLeasesRecheckEvent ev)
        {
            if (ev.Receiver != ev.Target.EventReceiver) yield break;
            foreach (Guid instanceId in simulationLeases
                .Where(entry => entry.Value.RequestToken.IsCancellationRequested)
                .Select(entry => entry.Key).ToArray())
            {
                yield return ev.Context.Factory.NewEvent<AbilitySimulationLeaseCanceledEvent>() with
                {
                    Target = ev.Target, InstanceId = instanceId,
                    Receiver = ev.Target.EventReceiver, TimeSpan = TimeSpan.Zero, IsScopeDetached = true
                };
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnSimulationLeaseCanceled(AbilitySimulationLeaseCanceledEvent ev)
        {
            if (ev.Receiver != ev.Target.EventReceiver ||
                !simulationLeases.TryGetValue(ev.InstanceId, out SimulationLease? simulation) ||
                !simulation.RequestToken.IsCancellationRequested)
                yield break;

            AbilityLeaseReference[] invalidated = simulation.AbilityIds
                .Where(id => activeLeases.TryGetValue(id, out Guid activeInstance) && activeInstance == ev.InstanceId)
                .Select(id => new AbilityLeaseReference(id, ev.InstanceId))
                .ToArray();
            if (invalidated.Length != simulation.AbilityIds.Count)
                throw new InvalidOperationException("The canceled simulation lease is inconsistent.");
            yield return ev.Context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
            {
                CommandEventId = simulation.CommandId,
                Source = simulation.Source,
                Target = ev.Target,
                AbilityIds = invalidated.Select(lease => lease.AbilityId).ToArray(),
                LeaseInstanceId = ev.InstanceId,
                Receiver = ev.Target.EventReceiver,
                IsScopeDetached = true
            };
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnUninstallRequest(AbilityUninstallRequestEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (ev.AbilityIds.Count == 0)
            {
                yield break;
            }
            RejectManagedDefinitionChanges(ev.Target, ev.AbilityIds);

            yield return ev.Context.Factory.NewEvent<AbilityUninstallEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = ev.Target,
                AbilityIds = ev.AbilityIds,
                LeaseInstanceId = ev.LeaseInstanceId,
                Receiver = ev.Target.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                Priority = ev.Priority,
                CancellationTokenSource = ev.CancellationTokenSource,
            };
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnUninstall(AbilityUninstallEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (ev.Receiver != ev.Target.EventReceiver)
            {
                yield break;
            }

            RejectManagedDefinitionChanges(ev.Target, ev.AbilityIds);
            UnitProperty before = ev.Target.Property;
            decimal previousEnergy = before.Energy;
            int previousEnergyMax = before.EnergyMax;
            // A delayed expiry must recheck the application that is mounted now.
            IReadOnlyList<string> abilityIds = ev.LeaseInstanceId is { } instanceId
                ? ev.AbilityIds.Where(id => activeLeases.TryGetValue(id, out Guid activeInstance)
                    && activeInstance == instanceId).Distinct(StringComparer.Ordinal).ToArray()
                : ev.AbilityIds;
            if (ev.LeaseInstanceId.HasValue && abilityIds.Count == 0)
            {
                yield break;
            }

            HashSet<string> idSet = abilityIds.ToHashSet(StringComparer.Ordinal);
            HashSet<string> protectedIds = ProtectedStatusIds(ev.Target);
            HashSet<string> blockedIds = protectedIds
                .Where(id => idSet.Any(requested => AbilityIdMatcher.IsSameOrChild(requested, id))
                    && !IsDueRealTimeLease(ev, id))
                .ToHashSet(StringComparer.Ordinal);
            if (blockedIds.Count > 0)
            {
                yield return ev.Context.Factory.NewEvent<AbilityUninstallRejectedEvent>() with
                {
                    CommandEventId = ev.CommandEventId,
                    Source = ev.Source,
                    Target = ev.Target,
                    ProtectedAbilityIds = blockedIds.ToArray(),
                    Receiver = ev.Receiver,
                    TimeSpan = TimeSpan.Zero,
                    Priority = ev.Priority
                };
            }

            IReadOnlyList<string> completedIds = blockedIds.Count == 0 ? abilityIds :
                abilityIds.Where(id =>
                    !blockedIds.Any(blocked => AbilityIdMatcher.IsSameOrChild(id, blocked)) ||
                    ev.Target.EventReceiver.GetHandlers<IHandler>().Any(handler =>
                        !blockedIds.Contains(handler.Id) && AbilityIdMatcher.IsSameOrChild(id, handler.Id)))
                .ToArray();
            if (completedIds.Count == 0)
                yield break;

            AbilityLeaseReference[] invalidated = activeLeases
                .Where(pair => ev.LeaseInstanceId.HasValue
                    ? idSet.Contains(pair.Key)
                    : idSet.Any(id => AbilityIdMatcher.IsSameOrChild(id, pair.Key)))
                .Where(pair => !blockedIds.Contains(pair.Key))
                .Select(pair => new AbilityLeaseReference(pair.Key, pair.Value))
                .ToArray();
            IReadOnlyList<IHandler> mountedHandlers = ev.Target.EventReceiver.GetHandlers<IHandler>();
            IHandler[] toRemove = mountedHandlers.Where(handler =>
                !blockedIds.Contains(handler.Id) &&
                (ev.LeaseInstanceId.HasValue
                    ? idSet.Contains(handler.Id)
                    : idSet.Any(id => AbilityIdMatcher.IsSameOrChild(id, handler.Id)))).ToArray();
            if (toRemove.Any(IsProtectedPreset))
                throw new InvalidOperationException("A preset ability handler cannot be removed.");
            var transaction = new AtomicTransaction();
            IReadOnlyList<IHandler> proposed = toRemove.Length == 0 ? mountedHandlers
                : ev.Target.EventReceiver.StageHandlerChanges(transaction, toRemove, []);
            if (toRemove.Length > 0 && ev.Causality is AbilityTriggerChain triggerChain)
                triggerChain.Router.StageChanges(transaction, [new TriggerRoutingChange(ev.Target, toRemove, [])]);
            ev.Target.Abilities.StageReApply(transaction, proposed);
            StageLeaseScopeCancellations(transaction, leaseState, invalidated);
            PeriodicEffectRegistry.PreparedChanges? periodicChanges =
                PrepareStatusRemoval(ev, transaction, ev.Target, toRemove.OfType<StatusAbility>());
            transaction.Commit();
            Guid operationId = ev.CommittedOperationId ?? Guid.NewGuid();
            if (!ev.CommittedOperationId.HasValue)
                ev.Context.MarkCurrentOperationCommitted(operationId);
            DetachChargeGates(toRemove);
            periodicChanges?.Publish();
            LeaseRemovalResult removal = RemoveLeaseRecords(invalidated);
            var cleanupFailures = new List<Exception>();
            if (periodicChanges != null)
                cleanupFailures.AddRange(periodicChanges.CloseRetired());
            cleanupFailures.AddRange(removal.Failures);
            ev.Target.Abilities.PublishProjection();
            if (ev.Target.Property.LifePoint is int life &&
                (life != before.LifePoint || ev.Target.Property.LifePointMax != before.LifePointMax))
                yield return UnitLifeChangedEvent.FromUnit(ev.Context, ev.Target, before.LifePoint ?? life)
                    with { IsScopeDetached = true, CommittedOperationId = operationId };
            if (ev.Target.Property.Energy != previousEnergy || ev.Target.Property.EnergyMax != previousEnergyMax)
                yield return UnitEnergyChangedEvent.FromUnit(ev.Context, ev.Target)
                    with { IsScopeDetached = true, CommittedOperationId = operationId };

            if (invalidated.Length > 0)
            {
                yield return CreateInvalidationEvent(ev, ev.Target, invalidated, removal.Completed)
                    with { IsScopeDetached = true, CommittedOperationId = operationId };
            }
            yield return ev.Context.Factory.NewEvent<AbilityUninstalledEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = ev.Target,
                AbilityIds = completedIds,
                ProjectionAlreadyApplied = true,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero,
                Priority = ev.Priority,
                CancellationTokenSource = ev.CancellationTokenSource,
                IsScopeDetached = true,
                CommittedOperationId = operationId,
            };
            ThrowLeaseCleanupFailures(cleanupFailures);
        }

        private bool IsDueRealTimeLease(AbilityUninstallEvent ev, string id) =>
            ev.LeaseInstanceId is { } instanceId &&
            realTimeLeases.TryGetValue(id, out RealTimeLease? lease) &&
            lease.InstanceId == instanceId &&
            ev.Context.TimeProvider.GetElapsedTime(lease.StartedAt) >= lease.Duration;

        private static void ValidateStatusApplication(
            Context context, Unit? source, Unit target, StatusApplicationOrigin? origin, Event currentEvent)
        {
            if (!context.ContainsReceiver(target.EventReceiver) ||
                !ReferenceEquals(target.EventReceiver.Context, context) ||
                source != null && (!context.ContainsReceiver(source.EventReceiver) ||
                    !ReferenceEquals(source.EventReceiver.Context, context)))
                throw new InvalidOperationException("Status participants must belong to this context.");
            (origin ?? throw new InvalidOperationException("A status application requires a real origin."))
                .Validate(context, currentEvent);
        }

        private static HashSet<string> ProtectedStatusIds(Unit target) =>
            target.EventReceiver.GetHandlers<StatusAbility>()
                .Where(status => !status.IsTriggerListener && status.ProtectedUntilExpiry)
                .Select(status => status.Id)
                .ToHashSet(StringComparer.Ordinal);

        private static void ValidateChargeGates(Context context, IEnumerable<IHandler> handlers)
        {
            var gates = new HashSet<CommandGateManager>(ReferenceEqualityComparer.Instance);
            foreach (AbilityBase ability in handlers.OfType<AbilityBase>())
            {
               if (ability is not IAbilityGate { GateManager: { } gate }) continue;
               if (!gates.Add(gate))
                   throw new InvalidOperationException("A charge gate cannot be shared between abilities.");
               gate.ValidateContextEntry(context, ability);
            }
        }

        private static void EnterChargeGates(Context context, Unit holder, IEnumerable<IHandler> handlers)
        {
            foreach (AbilityBase ability in handlers.OfType<AbilityBase>())
               if (ability is IAbilityGate { GateManager: { } gate })
               {
                   if (ability.Binding.SourceInstanceId != null) gate.BindAuthority(ability.Binding);
                   gate.EnterContext(context, ability, holder);
               }
        }

        private static void DetachChargeGates(IEnumerable<IHandler> handlers)
        {
            foreach (IAbilityGate ability in handlers.OfType<IAbilityGate>())
               ability.GateManager?.SetChargeHolder(null);
        }

        private static bool IsProtectedPreset(IHandler handler) =>
            handler is AbilityInstallHandler or HealingHandler or AbilityLifecycleHandler
                or ScheduledDamageHandler or ScheduledHealHandler or AbilityChargeRestoreHandler or UnitLevelUpHandler ||
            handler is TriggerHandler { IsPeriodicSettlement: true };

        private static PeriodicEffectRegistry.PreparedChanges? PrepareStatusRemoval(
            Event ev, AtomicTransaction transaction, Unit target, IEnumerable<StatusAbility> statuses)
        {
            HashSet<Guid> removed = statuses.Where(status => status.ApplicationId.HasValue)
                .Select(status => status.ApplicationId!.Value).ToHashSet();
            if (removed.Count == 0 || ev.Causality is not AbilityTriggerChain chain)
                return null;
            return chain.PeriodicEffects.PrepareChanges(transaction, chain, null, target,
                Array.Empty<PeriodicEffectApplication>(), removed, null, new HashSet<Guid>(),
                null, default(CancellationToken), out _, out _) ??
                throw new InvalidOperationException("Status periodic effect removal could not be prepared.");
        }

        private LeaseRemovalResult RemoveLeaseRecords(IEnumerable<AbilityLeaseReference> leases)
        {
            RetiredLeases retired = DetachLeaseRecords(leaseState, leases);
            return new LeaseRemovalResult(retired.Completed, CleanupRetiredLeases(retired));
        }

        private static void StageLeaseScopeCancellations(
            AtomicTransaction transaction, LeaseState state, IReadOnlyList<AbilityLeaseReference> invalidated)
        {
            foreach (AbilityLeaseReference lease in invalidated)
            {
                if (state.RealTime.TryGetValue(lease.AbilityId, out RealTimeLease? realTime) &&
                    realTime.InstanceId == lease.InstanceId)
                    realTime.Scope.StageCancellation(transaction);
            }
            foreach (var group in invalidated.GroupBy(lease => lease.InstanceId))
            {
                if (!state.Simulation.TryGetValue(group.Key, out SimulationLease? simulation))
                    continue;
                HashSet<string> removed = group.Select(lease => lease.AbilityId).ToHashSet(StringComparer.Ordinal);
                if (simulation.AbilityIds.All(removed.Contains))
                    simulation.Scope.StageCancellation(transaction);
            }
        }

        private static RetiredLeases DetachLeaseRecords(LeaseState state, IEnumerable<AbilityLeaseReference> leases)
        {
            var completed = new List<Guid>();
            var retiredRealTime = new List<RealTimeLease>();
            var retiredSimulations = new List<SimulationLease>();
            foreach (AbilityLeaseReference lease in leases)
            {
                state.Active.Remove(lease.AbilityId);
                if (state.RealTime.Remove(lease.AbilityId, out RealTimeLease? realTimeLease))
                    retiredRealTime.Add(realTimeLease);
                if (state.Simulation.TryGetValue(lease.InstanceId, out SimulationLease? simulation) &&
                    simulation.AbilityIds.Remove(lease.AbilityId) && simulation.AbilityIds.Count == 0)
                {
                    state.Simulation.Remove(lease.InstanceId);
                    completed.Add(lease.InstanceId);
                    retiredSimulations.Add(simulation);
                }
            }
            return new RetiredLeases(completed, retiredRealTime, retiredSimulations);
        }

        private static IReadOnlyList<Exception> CleanupRetiredLeases(RetiredLeases retired)
        {
            var failures = new List<Exception>();
            foreach (RealTimeLease lease in retired.RealTime)
            {
                lease.CancellationRegistration.Unregister();
                try { lease.Timer.Dispose(); }
                catch (Exception error) { failures.Add(error); }
                try { lease.Scope.Cancel(); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (SimulationLease simulation in retired.Simulation)
            {
                simulation.CancellationRegistration.Unregister();
                try { simulation.Scope.Cancel(); }
                catch (Exception error) { failures.Add(error); }
            }
            return failures;
        }

        private static void CleanupPreparedLeases(
            IReadOnlyList<EventScope> scopes, IReadOnlyList<ITimer> timers,
            IReadOnlyList<PreparedRealTimeLease> realTime, SimulationLease? simulation,
            CancellationTokenSource? ownedToken)
        {
            var failures = new List<Exception>();
            foreach (PreparedRealTimeLease prepared in realTime)
                prepared.Lease.CancellationRegistration.Unregister();
            if (simulation != null)
            {
                try { simulation.CancellationRegistration.Unregister(); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (ITimer timer in timers)
            {
                try { timer.Dispose(); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (EventScope scope in scopes)
            {
                try { scope.Cancel(); }
                catch (Exception error) { failures.Add(error); }
            }
            if (ownedToken != null)
            {
                try { ownedToken.Dispose(); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0)
                throw new AggregateException("Prepared ability leases failed to close.", failures);
        }

        private static void CleanupPreparedInstall(
            IReadOnlyList<EventScope> scopes, IReadOnlyList<ITimer> timers,
            IReadOnlyList<PreparedRealTimeLease> realTime, SimulationLease? simulation,
            CancellationTokenSource? ownedToken,
            PeriodicEffectRegistry.PreparedChanges? periodicChanges)
        {
            var failures = new List<Exception>();
            try { periodicChanges?.Abort(); }
            catch (Exception error) { failures.Add(error); }
            try { CleanupPreparedLeases(scopes, timers, realTime, simulation, ownedToken); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0)
                throw new AggregateException("Prepared ability installation failed to close.", failures);
        }

        private static void ThrowLeaseCleanupFailures(IReadOnlyList<Exception> failures)
        {
            if (failures.Count > 0)
                throw new AggregateException("One or more ability leases failed to close.", failures);
        }

        public void Dispose()
        {
            RealTimeLease[] realTime = realTimeLeases.Values.ToArray();
            SimulationLease[] simulations = simulationLeases.Values.ToArray();
            realTimeLeases.Clear();
            simulationLeases.Clear();
            activeLeases.Clear();
            List<Exception>? failures = null;
            foreach (RealTimeLease lease in realTime)
            {
                lease.CancellationRegistration.Unregister();
                try { lease.Timer.Dispose(); }
                catch (Exception error) { (failures ??= []).Add(error); }
                try { lease.Scope.Cancel(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            foreach (SimulationLease lease in simulations)
            {
                lease.CancellationRegistration.Unregister();
                try { lease.Scope.Cancel(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is { Count: > 0 })
                throw new AggregateException("One or more ability leases failed to close.", failures);
        }

        private static AbilityLeaseInvalidatedEvent CreateInvalidationEvent(
            Event ev, Unit target, IReadOnlyList<AbilityLeaseReference> leases,
            IReadOnlyList<Guid> completedSimulations)
            => ev.Context.Factory.NewEvent<AbilityLeaseInvalidatedEvent>() with
            {
                Target = target,
                Leases = leases,
                CompletedSimulationInstances = completedSimulations,
                Receiver = target.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                Priority = LeaseNotificationPriority,
            };

    }
}
