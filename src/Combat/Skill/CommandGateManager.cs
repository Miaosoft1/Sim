using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill
{
    public readonly record struct CommandGateCheckResult(
        bool CanExecute,
        TimeSpan Windup,
        CommandExecutionFailureReason FailureReason,
        TimeSpan? NextAvailableTime);

    public enum CommandReleaseResult
    {
        Committed,
        Canceled,
        ResourceInsufficient,
        AbilityNotAvailable,
        Casting,
        CoolingDown,
        ChargesDepleted,
        EffectRejected
    }

    /// <summary>
    /// Validates this ability's cooldown and tracks accepted activations.
    /// Unit-wide action restrictions belong to the unit action gate.
    /// </summary>
    public sealed class CommandGateManager
    {
        public AbilityType Type { get; init; } = AbilityType.Cast;

        /// <summary>
        /// Optional windup duration declared on this ability.
        /// </summary>
        public TimeSpan? Windup { get; init; }

        /// <summary>
        /// Backward compatibility alias for Windup.
        /// </summary>
        public TimeSpan? CastWindup
        {
            get => Windup;
            init => Windup = value;
        }

        /// <summary>
        /// Whether executing this ability occupies the caster's action/windup channel.
        /// Defaults to true. If false, this ability can be cast without occupying the caster
        /// and without being blocked by active casting windup (e.g. instant / off-GCD abilities).
        /// </summary>
        public bool OccupiesWindup { get; init; } = true;
        public bool ChecksActionRestrictions { get; init; } = true;

        /// <summary>
        /// Optional cooldown for this ability.
        /// </summary>
        public TimeSpan? Cooldown { get; init; }
        public AbilityChargeConfiguration? Charges { get; init; }

        /// <summary>
        /// Optional resource (mana/energy) cost from ability definition.
        /// </summary>
        private readonly int? resourceCost;
        public int? ResourceCost
        {
            get => resourceCost;
            init => resourceCost = value is < 0
                ? throw new ArgumentOutOfRangeException(nameof(ResourceCost))
                : value;
        }

        /// <summary>
        /// Runtime next-ready time after a successful release.
        /// </summary>
        public TimeSpan NextReadyTime
        {
            get { lock (sync) return committed.Value.NextReadyTime; }
        }
        public bool HasPendingReservation
        {
            get { lock (sync) return pending.Count != 0; }
        }
        private sealed class Reservation
        {
            public CancellationTokenRegistration Cancellation;
            public CancellationTokenRegistration OriginCancellation;
        }
        private readonly object sync = new();
        private readonly Dictionary<Guid, Reservation> pending = [];
        private sealed record CommittedState(TimeSpan NextReadyTime, TimeSpan PreviousReadyTime,
            Guid? CommandId, AbilityChargeState? Charges);
        private readonly AtomicCell<CommittedState> committed = new(new(TimeSpan.Zero, TimeSpan.Zero, null, null));
        private AbilitySourceBinding? authority;
        private AbilityChargeScheduler? chargeScheduler;
        private Context? chargeContext;
        private Guid chargeAbilityId;

        internal void ValidateContextEntry(Context context, AbilityBase ability)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(ability);
            ObjectDisposedException.ThrowIf(context.IsDisposed, context);
            if (ability is not IAbilityGate command || !ReferenceEquals(command.GateManager, this))
                throw new InvalidOperationException("The charge gate must belong to this ability.");
            if (authority != null && !ReferenceEquals(authority, ability.Binding))
                throw new InvalidOperationException("The gate is already bound to another ability.");
            if (Charges == null) return;
            lock (sync)
            {
                if (chargeContext != null &&
                    (!ReferenceEquals(chargeContext, context) || chargeAbilityId != ability.RuntimeInstanceId))
                    throw new InvalidOperationException("A charge gate cannot be reused by another ability or context.");
                if (ability.Binding.BoundContext != null && !ReferenceEquals(ability.Binding.BoundContext, context))
                    throw new InvalidOperationException("The ability belongs to another context.");
                if (chargeContext == null) _ = new AbilityChargeState(Charges, context.ContextTimeSpan);
            }
        }

        public void EnterContext(Context context, AbilityBase ability, Unit? holder = null)
        {
            ValidateContextEntry(context, ability);
            if (holder != null && (!ReferenceEquals(holder.EventReceiver.Context, context) ||
                !context.ContainsReceiver(holder.EventReceiver)))
                throw new InvalidOperationException("The ability holder must belong to this context.");
            if (Charges == null) return;
            lock (sync)
            {
                if (chargeContext != null)
                {
                    if (!ReferenceEquals(chargeContext, context) || chargeAbilityId != ability.RuntimeInstanceId)
                        throw new InvalidOperationException("A charge gate cannot be reused by another ability or context.");
                    chargeScheduler!.SetHolder(chargeAbilityId, holder);
                    return;
                }
                var prepared = new AbilityChargeState(Charges, context.ContextTimeSpan);
                var transaction = new AtomicTransaction();
                ability.Binding.StageContext(transaction, context);
                transaction.Commit();
                committed.Update(current => current with { Charges = prepared });
                chargeContext = context;
                chargeAbilityId = ability.RuntimeInstanceId;
                if (ability.Binding.SourceInstanceId != null)
                    BindAuthority(ability.Binding);
                chargeScheduler = AbilityChargeScheduler.For(context);
                chargeScheduler.Register(ability, this, holder);
            }
        }

        public AbilityChargeSnapshot? GetChargeSnapshot()
        {
            lock (sync)
            {
                if (Charges == null) return null;
                EnsureChargesEntered();
                return committed.Value.Charges!.Snapshot();
            }
        }

        internal TimeSpan? NextChargeRecovery
        {
            get { lock (sync) return committed.Value.Charges?.NextRecovery; }
        }

        internal void AdvanceCharges(TimeSpan now)
        {
            lock (sync) AdvanceChargesCore(now);
        }

        internal int RestoreCharges(TimeSpan now, int amount)
        {
            lock (sync)
            {
                EnsureChargesEntered();
                AdvanceChargesCore(now);
                AbilityChargeState charges = committed.Value.Charges!.Copy();
                int restored = charges.Restore(amount);
                if (restored > 0)
                {
                    committed.Update(state => state with { Charges = charges });
                    chargeScheduler!.Changed(chargeAbilityId);
                }
                return restored;
            }
        }

        internal void SetChargeHolder(Unit? holder)
        {
            lock (sync)
            {
                if (chargeScheduler != null)
                    chargeScheduler.SetHolder(chargeAbilityId, holder);
            }
        }

        private void EnsureChargesEntered()
        {
            if (committed.Value.Charges == null || chargeContext == null || chargeScheduler == null)
                throw new InvalidOperationException("The charged ability has not entered a context.");
            ObjectDisposedException.ThrowIf(chargeContext.IsDisposed, chargeContext);
        }

        private void AdvanceChargesCore(TimeSpan now)
        {
            if (Charges == null) return;
            EnsureChargesEntered();
            if (now != chargeContext!.ContextTimeSpan)
                throw new InvalidOperationException("Charges use their owning context's current simulation time.");
            AbilityChargeState current = committed.Value.Charges!;
            bool recovered = current.NextRecovery is { } deadline && deadline <= now;
            if (recovered)
            {
                AbilityChargeState charges = current.Copy();
                if (!charges.Advance(now))
                    throw new InvalidOperationException("The charge recovery deadline changed unexpectedly.");
                committed.Update(state => state with { Charges = charges });
            }
            chargeScheduler!.ResumeTimer(chargeAbilityId);
            if (recovered)
                chargeScheduler!.Changed(chargeAbilityId);
        }

        public void BindAuthority(AbilitySourceBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            lock (sync)
            {
                if (authority != null && !ReferenceEquals(authority, binding))
                    throw new InvalidOperationException("The gate is already bound to another ability.");
                authority = binding;
            }
        }

        public void RefundCooldown(Guid commandId)
        {
            lock (sync)
            {
                CommittedState current = committed.Value;
                if (current.CommandId == commandId)
                {
                    committed.Set(current with { NextReadyTime = current.PreviousReadyTime, CommandId = null });
                }
            }
        }

        public bool HasTiming
            => ToNonNegative(Windup) > TimeSpan.Zero
            || ToNonNegative(Cooldown) > TimeSpan.Zero;

        public CommandGateCheckResult Check(TimeSpan now, TimeSpan? handlerWindup)
        {
            lock (sync) return CheckCore(now, handlerWindup);
        }

        private CommandGateCheckResult CheckCore(TimeSpan now, TimeSpan? handlerWindup)
        {
            AdvanceChargesCore(now);
            TimeSpan effectiveWindup = handlerWindup ?? Windup ?? TimeSpan.Zero;
            effectiveWindup = ToNonNegative(effectiveWindup);

            CommittedState current = committed.Value;
            if (now < current.NextReadyTime)
            {
                return new CommandGateCheckResult(false, TimeSpan.Zero, CommandExecutionFailureReason.CoolingDown, current.NextReadyTime);
            }

            if (current.Charges is { Available: 0 } charges)
                return new CommandGateCheckResult(false, TimeSpan.Zero,
                    CommandExecutionFailureReason.ChargesDepleted, charges.NextRecovery);

            return new CommandGateCheckResult(true, effectiveWindup, CommandExecutionFailureReason.None, null);
        }

        public CommandGateCheckResult ValidateAndReserve(
            TimeSpan now, TimeSpan? handlerWindup, Guid commandId, CancellationToken cancellationToken = default,
            EventCancellation originCancellationToken = default)
        {
            lock (sync)
            {
                CommandGateCheckResult result = CheckCore(now, handlerWindup);
                if (!result.CanExecute) return result;

                if (commandId == Guid.Empty || pending.ContainsKey(commandId))
                    throw new InvalidOperationException("The activation must have a unique nonempty identity.");
                var reservation = new Reservation();
                pending.Add(commandId, reservation);
                if (cancellationToken.CanBeCanceled)
                {
                    CancellationTokenRegistration registration = cancellationToken.Register(() => CancelReservation(commandId));
                    if (pending.TryGetValue(commandId, out Reservation? current) && ReferenceEquals(current, reservation))
                        reservation.Cancellation = registration;
                    else
                        registration.Unregister();
                }
                if (pending.ContainsKey(commandId) && originCancellationToken.CanBeCanceled)
                {
                    CancellationTokenRegistration registration = originCancellationToken.Register(() => CancelReservation(commandId));
                    if (pending.TryGetValue(commandId, out Reservation? current) && ReferenceEquals(current, reservation))
                        reservation.OriginCancellation = registration;
                    else
                        registration.Unregister();
                }
                return result;
            }
        }

        public bool Commit(Guid commandId, TimeSpan now, CancellationToken cancellationToken = default,
            EventCancellation originCancellationToken = default)
        {
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested || originCancellationToken.IsCancellationRequested)
                {
                    CancelReservationCore(commandId);
                    return false;
                }
                if (!pending.ContainsKey(commandId))
                    throw new InvalidOperationException("The ability has no matching pending reservation.");
                CommandGateCheckResult check = CheckCore(now, null);
                if (!check.CanExecute)
                    throw new InvalidOperationException($"The ability cannot release: {check.FailureReason}.");
                CommittedState current = committed.Value;
                TimeSpan? deadline = current.Charges?.PrepareConsumption(now);
                AbilityChargeState? charges = current.Charges?.Copy();
                charges?.Consume(deadline);
                TimeSpan cooldown = ToNonNegative(Cooldown);
                TimeSpan ready = cooldown > TimeSpan.Zero ? now.Add(cooldown) : now;
                CancelReservationCore(commandId);
                committed.Set(new CommittedState(ready, current.NextReadyTime, commandId, charges));
                if (charges != null)
                {
                    chargeScheduler!.Changed(chargeAbilityId);
                }
                return true;
            }
        }

        public CommandReleaseResult CommitRelease(Unit source, Guid commandId, TimeSpan now,
            CancellationToken cancellationToken = default, EventCancellation originCancellationToken = default) =>
            CommitReleaseCore(source, commandId, now, cancellationToken, originCancellationToken, null, null);

        internal CommandReleaseResult CommitRelease(Unit source, Guid commandId, TimeSpan now,
            CancellationToken cancellationToken, EventCancellation originCancellationToken, Event releaseEvent) =>
            CommitReleaseCore(source, commandId, now, cancellationToken, originCancellationToken, releaseEvent, null);

        internal CommandReleaseResult CommitRelease(Unit source, Guid commandId, TimeSpan now,
            CancellationToken cancellationToken, EventCancellation originCancellationToken, Event releaseEvent,
            Func<AtomicTransaction, bool> prepareEffects) =>
            CommitReleaseCore(source, commandId, now, cancellationToken, originCancellationToken, releaseEvent,
                prepareEffects ?? throw new ArgumentNullException(nameof(prepareEffects)));

        private CommandReleaseResult CommitReleaseCore(Unit source, Guid commandId, TimeSpan now,
            CancellationToken cancellationToken, EventCancellation originCancellationToken, Event? releaseEvent,
            Func<AtomicTransaction, bool>? prepareEffects)
        {
            if (cancellationToken.IsCancellationRequested || originCancellationToken.IsCancellationRequested)
            {
                CancelReservation(commandId);
                return CommandReleaseResult.Canceled;
            }

            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested || originCancellationToken.IsCancellationRequested)
                {
                    CancelReservationCore(commandId);
                    return CommandReleaseResult.Canceled;
                }
                if (authority != null && !authority.IsAuthorized(source.Id))
                {
                    CancelReservationCore(commandId);
                    return CommandReleaseResult.AbilityNotAvailable;
                }
                if (!pending.ContainsKey(commandId))
                    throw new InvalidOperationException("The ability has no matching pending reservation.");
                CommandGateCheckResult check = CheckCore(now, null);
                if (!check.CanExecute)
                {
                    CancelReservationCore(commandId);
                    return check.FailureReason switch
                    {
                        CommandExecutionFailureReason.CoolingDown => CommandReleaseResult.CoolingDown,
                        CommandExecutionFailureReason.ChargesDepleted => CommandReleaseResult.ChargesDepleted,
                        _ => throw new InvalidOperationException("Unexpected ability gate failure.")
                    };
                }
                UnitProperty property = source.Property;
                int cost = ResourceCost ?? 0;
                if (property.Energy < cost)
                {
                    CancelReservationCore(commandId);
                    return CommandReleaseResult.ResourceInsufficient;
                }
                CommittedState current = committed.Value;
                TimeSpan? deadline = current.Charges?.PrepareConsumption(now);
                AbilityChargeState? charges = current.Charges?.Copy();
                charges?.Consume(deadline);
                TimeSpan cooldown = ToNonNegative(Cooldown);
                TimeSpan ready = cooldown > TimeSpan.Zero ? now.Add(cooldown) : now;
                var transaction = new AtomicTransaction();
                transaction.Stage(committed, new CommittedState(ready, current.NextReadyTime, commandId, charges));
                source.Abilities.StageEnergyCost(transaction, cost);
                bool effectsAccepted;
                try { effectsAccepted = prepareEffects?.Invoke(transaction) ?? true; }
                catch
                {
                    CancelReservationCore(commandId);
                    throw;
                }
                if (cancellationToken.IsCancellationRequested || originCancellationToken.IsCancellationRequested)
                {
                    CancelReservationCore(commandId);
                    return CommandReleaseResult.Canceled;
                }
                if (!effectsAccepted)
                {
                    CancelReservationCore(commandId);
                    return CommandReleaseResult.EffectRejected;
                }
                try { transaction.Commit(); }
                catch
                {
                    CancelReservationCore(commandId);
                    if (committed.Value.CommandId == commandId && releaseEvent != null &&
                        !releaseEvent.CommittedOperationId.HasValue)
                        releaseEvent.Context.MarkCurrentOperationCommitted(commandId);
                    throw;
                }
                CancelReservationCore(commandId);
                if (releaseEvent != null && !releaseEvent.CommittedOperationId.HasValue)
                    releaseEvent.Context.MarkCurrentOperationCommitted(commandId);
                if (charges != null) chargeScheduler!.Changed(chargeAbilityId);
                return CommandReleaseResult.Committed;
            }
        }

        internal bool WasCommitted(Guid commandId)
        {
            lock (sync) return committed.Value.CommandId == commandId;
        }

        public void CancelReservation(Guid commandId)
        {
            lock (sync) CancelReservationCore(commandId);
        }

        private void CancelReservationCore(Guid commandId)
        {
            if (!pending.Remove(commandId, out Reservation? reservation)) return;
            reservation.Cancellation.Unregister();
            reservation.OriginCancellation.Unregister();
        }

        private static TimeSpan ToNonNegative(TimeSpan? input)
        {
            if (!input.HasValue || input.Value <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return input.Value;
        }
    }
}
