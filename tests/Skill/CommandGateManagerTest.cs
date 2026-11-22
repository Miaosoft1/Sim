using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class CommandGateManagerTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void ValidateAndReserve_Cooldown_EnforcesCooldownWindow()
    {
        var (context, unit) = CreateUnit();
        CommandGateManager gate = new()
        {
            Type = AbilityType.Cast,
            Cooldown = TimeSpan.FromSeconds(2),
        };

        CommandGateCheckResult first = Request(context, unit, gate, TimeSpan.Zero, out Guid firstId);
        Assert.IsTrue(first.CanExecute);
        Assert.AreEqual(TimeSpan.Zero, gate.NextReadyTime);
        Assert.IsTrue(gate.Check(TimeSpan.Zero, null).CanExecute);
        gate.Commit(firstId, TimeSpan.Zero);
        Assert.AreEqual(TimeSpan.FromSeconds(2), gate.NextReadyTime);

        CommandGateCheckResult second = Request(context, unit, gate, TimeSpan.FromSeconds(1));
        Assert.IsFalse(second.CanExecute);
        Assert.AreEqual(CommandExecutionFailureReason.CoolingDown, second.FailureReason);
        Assert.AreEqual(TimeSpan.FromSeconds(2), second.NextAvailableTime);

        CommandGateCheckResult third = Request(context, unit, gate, TimeSpan.FromSeconds(2), out Guid thirdId);
        Assert.IsTrue(third.CanExecute);
        gate.Commit(thirdId, TimeSpan.FromSeconds(2));
        Assert.AreEqual(TimeSpan.FromSeconds(4), gate.NextReadyTime);
    }

    [TestMethod]
    public void ValidateAndReserve_OccupiesWindup_LocksCasterAndRejectsSimultaneousCast()
    {
        var (context, unit) = CreateUnit();
        CommandGateManager heavy = new() { Windup = TimeSpan.FromSeconds(0.5), OccupiesWindup = true };
        CommandGateManager second = new() { Windup = TimeSpan.FromSeconds(0.3), OccupiesWindup = true };

        CommandGateCheckResult first = Request(context, unit, heavy, TimeSpan.Zero);
        Assert.IsTrue(first.CanExecute);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), first.Windup);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), unit.ActionGate!.GetNextAvailableTime(TimeSpan.Zero));

        CommandGateCheckResult rejected = Request(context, unit, second, TimeSpan.FromSeconds(0.2));
        Assert.IsFalse(rejected.CanExecute);
        Assert.AreEqual(CommandExecutionFailureReason.Casting, rejected.FailureReason);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), rejected.NextAvailableTime);

        CommandGateCheckResult accepted = Request(context, unit, second, TimeSpan.FromSeconds(0.5));
        Assert.IsTrue(accepted.CanExecute);
        Assert.AreEqual(TimeSpan.FromSeconds(0.8), unit.ActionGate.GetNextAvailableTime(TimeSpan.FromSeconds(0.5)));
    }

    [TestMethod]
    public void ValidateAndReserve_NotOccupyingWindup_CanCastDuringWindupWithoutLocking()
    {
        var (context, unit) = CreateUnit();
        CommandGateManager heavy = new() { Windup = TimeSpan.FromSeconds(0.5), OccupiesWindup = true };
        CommandGateManager instant = new()
        {
            OccupiesWindup = false,
            Cooldown = TimeSpan.FromSeconds(1),
        };

        Assert.IsTrue(Request(context, unit, heavy, TimeSpan.Zero).CanExecute);
        CommandGateCheckResult result = Request(context, unit, instant, TimeSpan.FromSeconds(0.2), out Guid instantId);
        Assert.IsTrue(result.CanExecute);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), unit.ActionGate!.GetNextAvailableTime(TimeSpan.FromSeconds(0.2)));
        instant.Commit(instantId, TimeSpan.FromSeconds(0.2));
        Assert.AreEqual(TimeSpan.FromSeconds(1.2), instant.NextReadyTime);
    }

    [TestMethod]
    public void Availability_PrioritizesUnitRestrictionsBeforeAbilityCooldown()
    {
        var (context, unit) = CreateUnit();
        CommandGateManager gate = new()
        {
            Windup = TimeSpan.FromSeconds(0.5),
            Cooldown = TimeSpan.FromSeconds(2),
        };
        Assert.IsTrue(Request(context, unit, gate, TimeSpan.Zero, out Guid commandId).CanExecute);
        StatusAbility stun = new()
        {
            Id = "test-stun",
            Labels = AbilityLabel.Stunned,
        };
        TestHandlerFactory.Register(unit, stun);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            Source = unit,
            Target = unit,
            AbilityIds = new[] { stun.Id },
            ExpireAt = TimeSpan.FromSeconds(0.3),
            Receiver = unit.EventReceiver,
            TimeSpan = TimeSpan.Zero,
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(0.1));

        CommandGateCheckResult stunned = unit.ActionGate!.CheckAvailability(context.ContextTimeSpan, unit, gate);
        Assert.AreEqual(CommandExecutionFailureReason.CastForbidden, stunned.FailureReason);
        Assert.AreEqual(TimeSpan.FromSeconds(0.3), stunned.NextAvailableTime);
        Assert.IsTrue(gate.Check(context.ContextTimeSpan, null).CanExecute);

        context.AdvanceTo(TimeSpan.FromSeconds(0.3));
        CommandGateCheckResult casting = unit.ActionGate.CheckAvailability(context.ContextTimeSpan, unit, gate);
        Assert.AreEqual(CommandExecutionFailureReason.Casting, casting.FailureReason);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), casting.NextAvailableTime);

        context.AdvanceTo(TimeSpan.FromSeconds(0.5));
        gate.Commit(commandId, context.ContextTimeSpan);
        CommandGateCheckResult coolingDown = unit.ActionGate.CheckAvailability(context.ContextTimeSpan, unit, gate);
        Assert.AreEqual(CommandExecutionFailureReason.CoolingDown, coolingDown.FailureReason);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), coolingDown.NextAvailableTime);
        context.AdvanceTo(TimeSpan.FromSeconds(2.5));
        Assert.IsTrue(unit.ActionGate.CheckAvailability(context.ContextTimeSpan, unit, gate).CanExecute);
        TestContext.WriteLine("availability order: stun, action channel, ability cooldown, ready");
    }

    [TestMethod]
    public void CancelledReservationDoesNotStartCooldownOrRefundANewerCommand()
    {
        var gate = new CommandGateManager { Windup = TimeSpan.FromSeconds(1), Cooldown = TimeSpan.FromSeconds(5) };
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, first, cancellation.Token).CanExecute);
        cancellation.Cancel();
        Assert.AreEqual(TimeSpan.Zero, gate.NextReadyTime);
        Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, second).CanExecute);
        gate.Commit(second, TimeSpan.FromSeconds(1));
        gate.RefundCooldown(first);
        Assert.AreEqual(TimeSpan.FromSeconds(6), gate.NextReadyTime);
        Assert.ThrowsExactly<InvalidOperationException>(() => gate.Commit(first, TimeSpan.FromSeconds(1)));
        TestContext.WriteLine("Cancellation released the windup reservation; a stale command could not refund a newer cooldown.");
    }

    [TestMethod]
    public void IndependentOffChannelActivationsCanBothReleaseWithoutConfiguredCooldown()
    {
        var (context, unit) = CreateUnit();
        using (context)
        {
            unit.Property.Energy = 10;
            var gate = new CommandGateManager { OccupiesWindup = false, ResourceCost = 2 };
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();

            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, first).CanExecute);
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, second).CanExecute);
            Assert.AreEqual(CommandReleaseResult.Committed, gate.CommitRelease(unit, first, TimeSpan.Zero));
            Assert.AreEqual(CommandReleaseResult.Committed, gate.CommitRelease(unit, second, TimeSpan.Zero));
            Assert.AreEqual(6, unit.Property.Energy);
            Assert.IsFalse(gate.HasPendingReservation);
            TestContext.WriteLine("Two off-channel activations committed independently without an unconfigured reservation lock.");
        }
    }

    [TestMethod]
    public void LaterAcceptedActivationRechecksCooldownAndDoesNotSpendResources()
    {
        var (context, unit) = CreateUnit();
        using (context)
        {
            unit.Property.Energy = 10;
            var gate = new CommandGateManager
            {
                OccupiesWindup = false, ResourceCost = 2, Cooldown = TimeSpan.FromSeconds(5)
            };
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, first).CanExecute);
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, second).CanExecute);

            Assert.AreEqual(CommandReleaseResult.Committed, gate.CommitRelease(unit, first, TimeSpan.Zero));
            Assert.AreEqual(CommandReleaseResult.CoolingDown, gate.CommitRelease(unit, second, TimeSpan.Zero));
            Assert.AreEqual(8, unit.Property.Energy);
            Assert.AreEqual(TimeSpan.FromSeconds(5), gate.NextReadyTime);
            Assert.IsFalse(gate.HasPendingReservation);
            TestContext.WriteLine("The second activation reached release but failed the now-active cooldown without spending energy.");
        }
    }

    [TestMethod]
    public void StagedEnergyCostSurvivesAPropertyReapplicationOnTheSameUnit()
    {
        var (context, unit) = CreateUnit();
        using (context)
        {
            unit.Property.Energy = 90;
            unit.Abilities.BaseProperty.EnergyMax = 50;
            var transaction = new AtomicTransaction();
            unit.Abilities.StageEnergyCost(transaction, 20);
            unit.Abilities.StageReApply(transaction, unit.EventReceiver.GetHandlers<IHandler>(), 2);

            Assert.AreEqual(90m, unit.Property.Energy);
            Assert.AreEqual(100, unit.Property.EnergyMax);
            transaction.Commit();
            Assert.AreEqual(50m, unit.Property.Energy);
            Assert.AreEqual(50, unit.Property.EnergyMax);
            Assert.AreEqual(2, unit.Property.Level);
            TestContext.WriteLine("The 20 energy cost was staged before the new 50-point maximum was applied.");
        }
    }

    [TestMethod]
    public void StalePropertyProjectionRejectsTheStagedEnergyCost()
    {
        var (context, unit) = CreateUnit();
        using (context)
        {
            unit.Property.Energy = 90;
            var transaction = new AtomicTransaction();
            unit.Abilities.StageEnergyCost(transaction, 20);
            UnitProperty replacement = unit.Property.Copy();
            replacement.Energy = 80;
            unit.Abilities.Property = replacement;

            Assert.ThrowsExactly<InvalidOperationException>(transaction.Commit);
            Assert.AreEqual(80m, unit.Property.Energy);
            TestContext.WriteLine("A stale energy projection could not overwrite the newer unit property.");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RejectedOrCanceledPreparationDoesNotCommitTheGate(bool cancel)
    {
        var (context, unit) = CreateUnit();
        using (context)
        using (var cancellation = new CancellationTokenSource())
        {
            unit.Property.Energy = 30;
            var gate = new CommandGateManager
            {
                ResourceCost = 10, Cooldown = TimeSpan.FromSeconds(5),
                Charges = new AbilityChargeConfiguration(1, 1)
            };
            var ability = new ProjectileAbility { Id = "staged-release", GateManager = gate };
            gate.EnterContext(context, ability, unit);
            Guid activation = Guid.NewGuid();
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, activation, cancellation.Token).CanExecute);
            var effect = new AtomicCell<int>(0);

            CommandReleaseResult result = gate.CommitRelease(unit, activation, TimeSpan.Zero,
                cancellation.Token, default, context.Factory.NewEvent<Event>(), transaction =>
                {
                    transaction.Stage(effect, 1);
                    if (cancel) cancellation.Cancel();
                    return cancel;
                });

            Assert.AreEqual(cancel ? CommandReleaseResult.Canceled : CommandReleaseResult.EffectRejected, result);
            Assert.AreEqual(0, effect.Value);
            Assert.AreEqual(30m, unit.Property.Energy);
            Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
            Assert.AreEqual(TimeSpan.Zero, gate.NextReadyTime);
            Assert.IsFalse(gate.HasPendingReservation);
            TestContext.WriteLine("The staged effect, energy, charge and cooldown remained unchanged after rejection or cancellation.");
        }
    }

    [TestMethod]
    public void FailedEffectPreparationCancelsItsGateReservation()
    {
        var (context, unit) = CreateUnit();
        using (context)
        {
            unit.Property.Energy = 30;
            var gate = new CommandGateManager { ResourceCost = 10, Cooldown = TimeSpan.FromSeconds(5) };
            Guid activation = Guid.NewGuid();
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, activation).CanExecute);
            var effect = new AtomicCell<int>(0);
            Assert.ThrowsExactly<InvalidOperationException>(() => gate.CommitRelease(
                unit, activation, TimeSpan.Zero, default, default, context.Factory.NewEvent<Event>(), transaction =>
                {
                    transaction.Stage(effect, 1);
                    throw new InvalidOperationException("effect preparation failed");
                }));
            Assert.AreEqual(0, effect.Value);
            Assert.AreEqual(30m, unit.Property.Energy);
            Assert.AreEqual(TimeSpan.Zero, gate.NextReadyTime);
            Assert.IsFalse(gate.HasPendingReservation);
            TestContext.WriteLine("A preparation exception left the staged effect and gate resources unchanged.");
        }
    }

    [TestMethod]
    public void CancelingOneAcceptedActivationDoesNotCancelAnother()
    {
        var (context, unit) = CreateUnit();
        using (context)
        using (var cancellation = new CancellationTokenSource())
        {
            var gate = new CommandGateManager { OccupiesWindup = false };
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, first, cancellation.Token).CanExecute);
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, second).CanExecute);
            cancellation.Cancel();
            Assert.IsTrue(gate.HasPendingReservation);
            Assert.AreEqual(CommandReleaseResult.Committed, gate.CommitRelease(unit, second, TimeSpan.Zero));
            Assert.IsFalse(gate.HasPendingReservation);
            TestContext.WriteLine("Canceling one accepted activation preserved the other activation.");
        }
    }

    [TestMethod]
    public void TransferDoesNotLetAnOldActivationBlockOrUndoTheNewOwnersRelease()
    {
        var (context, firstOwner) = CreateUnit();
        using (context)
        {
            Unit secondOwner = CreateAdditionalUnit(context);
            firstOwner.Property.EnergyMax = secondOwner.Property.EnergyMax = 100;
            firstOwner.Property.Energy = secondOwner.Property.Energy = 20;
            var binding = new AbilitySourceBinding();
            var initial = new AtomicTransaction();
            binding.StageBind(initial, new TestAbilitySource(Guid.NewGuid()));
            binding.StageAuthority(initial, context, firstOwner.Id);
            initial.Commit();
            var gate = new CommandGateManager
            {
                OccupiesWindup = false, ResourceCost = 5, Cooldown = TimeSpan.FromSeconds(5)
            };
            gate.BindAuthority(binding);
            Guid oldActivation = Guid.NewGuid(), newActivation = Guid.NewGuid();
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, oldActivation).CanExecute);
            var transfer = new AtomicTransaction();
            binding.StageAuthority(transfer, context, secondOwner.Id);
            transfer.Commit();
            Assert.IsTrue(gate.ValidateAndReserve(TimeSpan.Zero, null, newActivation).CanExecute);
            Assert.AreEqual(CommandReleaseResult.Committed, gate.CommitRelease(secondOwner, newActivation, TimeSpan.Zero));

            var returnTransfer = new AtomicTransaction();
            binding.StageAuthority(returnTransfer, context, firstOwner.Id);
            returnTransfer.Commit();
            Assert.AreEqual(CommandReleaseResult.CoolingDown, gate.CommitRelease(firstOwner, oldActivation, TimeSpan.Zero));
            gate.RefundCooldown(oldActivation);
            Assert.AreEqual(TimeSpan.FromSeconds(5), gate.NextReadyTime);
            Assert.AreEqual(20m, firstOwner.Property.Energy);
            Assert.AreEqual(15m, secondOwner.Property.Energy);
            Assert.IsFalse(gate.HasPendingReservation);
            TestContext.WriteLine("The new owner released during the old windup; the returned old activation respected the new cooldown.");
        }
    }

    [TestMethod]
    public void AbilityBindingKeepsTheRealSourceAndSnapshotsItsCategory()
    {
        var (context, owner) = CreateUnit();
        using (context)
        {
            var source = new MutableAbilitySource { InstanceId = Guid.NewGuid(), Category = "Equipment" };
            var ability = new ProjectileAbility { Id = "bound" };
            var transaction = new AtomicTransaction();
            ability.Binding.StageBind(transaction, source);
            ability.Binding.StageAuthority(transaction, context, owner.Id);
            transaction.Commit();
            AbilityRuntimeSnapshot snapshot = AbilityRuntimeSnapshot.Capture(ability);

            var sameSource = new AtomicTransaction();
            ability.Binding.StageBind(sameSource, source);
            sameSource.Commit();
            Assert.AreSame(source, ability.Binding.Source);
            Assert.AreEqual(source.InstanceId, snapshot.GrantSourceInstanceId);
            Assert.AreEqual("Equipment", snapshot.GrantSourceCategory);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ability.Binding.StageBind(new AtomicTransaction(),
                    new TestAbilitySource(source.InstanceId, "Equipment")));

            source.Category = "Changed";
            Assert.ThrowsExactly<InvalidOperationException>(() => AbilityRuntimeSnapshot.Capture(ability));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                ability.Binding.StageAuthority(new AtomicTransaction(), context, owner.Id));
            Assert.AreEqual("Equipment", snapshot.GrantSourceCategory);
            Assert.AreEqual(owner.Id, ability.Binding.OwnerId);
            TestContext.WriteLine("A source retained its real object and snapshot category; impostors and identity mutation were rejected.");
        }
    }

    [TestMethod]
    public void AbilityBindingRejectsAnInvalidSourceIdentityOrCategory()
    {
        var binding = new AbilitySourceBinding();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            binding.StageBind(new AtomicTransaction(), new TestAbilitySource(Guid.Empty, "Equipment")));
        foreach (string category in new[] { "", " ", " Equipment" })
            Assert.ThrowsExactly<ArgumentException>(() =>
                binding.StageBind(new AtomicTransaction(), new TestAbilitySource(Guid.NewGuid(), category)));
        Assert.IsNull(binding.Source);
        TestContext.WriteLine("Empty source IDs and empty or unnormalized categories failed before binding.");
    }

    private sealed class MutableAbilitySource : IAbilitySource
    {
        public Guid InstanceId { get; set; }
        public string Category { get; set; } = "";
    }

    [TestMethod]
    public void OneSourceCanBindMultiplePartsButCannotBeImpersonatedWithinTheContext()
    {
        var (context, owner) = CreateUnit();
        using (context)
        {
            var source = new TestAbilitySource(Guid.NewGuid(), "Equipment");
            var first = new AbilitySourceBinding();
            var second = new AbilitySourceBinding();
            var transaction = new AtomicTransaction();
            foreach (var binding in new[] { first, second })
            {
                binding.StageBind(transaction, source);
                binding.StageAuthority(transaction, context, owner.Id);
            }
            transaction.Commit();
            Assert.AreSame(source, first.Source);
            Assert.AreSame(source, second.Source);

            var lateBinding = new AbilitySourceBinding();
            var enter = new AtomicTransaction();
            lateBinding.StageContext(enter, context);
            enter.Commit();
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                lateBinding.StageBind(new AtomicTransaction(), new TestAbilitySource(source.InstanceId, "Equipment")));
            Assert.IsNull(lateBinding.Source);
            var valid = new AtomicTransaction();
            lateBinding.StageBind(valid, source);
            valid.Commit();
            Assert.AreSame(source, lateBinding.Source);
            TestContext.WriteLine("Multiple parts shared one real source; a new binding could not impersonate its ID, including after context entry.");
        }
    }

    [TestMethod]
    public void CompetingBindingsCannotMoveOneRealSourceAcrossContexts()
    {
        using var firstContext = new Context();
        using var secondContext = new Context();
        var source = new TestAbilitySource(Guid.NewGuid());
        var first = new AbilitySourceBinding();
        var second = new AbilitySourceBinding();
        var accepted = new AtomicTransaction();
        var rejected = new AtomicTransaction();
        first.StageBind(accepted, source);
        first.StageContext(accepted, firstContext);
        second.StageBind(rejected, source);
        second.StageContext(rejected, secondContext);

        accepted.Commit();
        Assert.ThrowsExactly<InvalidOperationException>(() => rejected.Commit());
        Assert.IsNull(second.Source);
        Assert.IsNull(second.BoundContext);
        var retry = new AtomicTransaction();
        second.StageBind(retry, source);
        Assert.ThrowsExactly<InvalidOperationException>(() => second.StageContext(retry, secondContext));

        var independent = new AtomicTransaction();
        var otherSource = new TestAbilitySource(source.InstanceId);
        second.StageBind(independent, otherSource);
        second.StageContext(independent, secondContext);
        independent.Commit();
        Assert.AreSame(firstContext, first.BoundContext);
        Assert.AreSame(secondContext, second.BoundContext);
        Assert.AreSame(otherSource, second.Source);
        TestContext.WriteLine("Two prepared context entries competed for one source; only one committed. A distinct object may reuse the ID in another context.");
    }

    [TestMethod]
    public void FailedTransactionDoesNotClaimTheSourceIdentityOrContext()
    {
        using var firstContext = new Context();
        using var secondContext = new Context();
        var source = new TestAbilitySource(Guid.NewGuid());
        var binding = new AbilitySourceBinding();
        var conflict = new AtomicCell<int>(0);
        var rejected = new AtomicTransaction();
        binding.StageBind(rejected, source);
        binding.StageContext(rejected, firstContext);
        rejected.Stage(conflict, 1);
        conflict.Set(2);
        Assert.ThrowsExactly<InvalidOperationException>(() => rejected.Commit());
        Assert.IsNull(binding.Source);
        Assert.IsNull(binding.BoundContext);

        var retry = new AtomicTransaction();
        binding.StageBind(retry, source);
        binding.StageContext(retry, secondContext);
        var replacement = new AbilitySourceBinding();
        replacement.StageBind(retry, new TestAbilitySource(source.InstanceId));
        replacement.StageContext(retry, firstContext);
        retry.Commit();
        Assert.AreSame(secondContext, binding.BoundContext);
        Assert.AreSame(firstContext, replacement.BoundContext);
        Assert.AreEqual(2, conflict.Value);
        TestContext.WriteLine("A failed pre-commit left no source claim; both its identity slot and original object remained available to valid transactions.");
    }

    [TestMethod]
    public void SimultaneousActionsKeepTheirOwnCausalityThroughTheSharedFlush()
    {
        var (context, unit) = CreateUnit(new TriggerChainFactory());
        using (context)
        {
            context.Logger.Output = null;
            UnitActionGateRequestEvent first = QueueAction(context, unit);
            UnitActionGateRequestEvent second = QueueAction(context, unit);
            context.RunUntilQueueEmpty();

            UnitActionGateResultEvent[] results = context.History.OfType<UnitActionGateResultEvent>().ToArray();
            Assert.HasCount(2, results);
            Assert.AreNotSame(first.Causality, second.Causality);
            Assert.AreSame(first.Causality, results.Single(result => result.Request == first).Causality);
            Assert.AreSame(second.Causality, results.Single(result => result.Request == second).Causality);
            TestContext.WriteLine("Two independent actions shared a gate flush without sharing their trigger chains.");
        }
    }

    private static UnitActionGateRequestEvent QueueAction(Context context, Unit unit)
    {
        var command = context.Factory.NewEvent<CommandEvent>() with
        { AbilityId = "test", Source = unit, Receiver = unit.EventReceiver };
        var request = context.Factory.NewEvent<UnitActionGateRequestEvent>() with
        {
            Source = unit, Origin = command, CommandId = command.CommandInstanceId, AbilityId = command.AbilityId,
            CheckRestrictions = false, ActionCancellation = new CancellationTokenSource(), Receiver = unit.EventReceiver
        };
        context.Dispatch(request).ToList();
        return request;
    }

    private CommandGateCheckResult Request(Context context, Unit unit, CommandGateManager gate, TimeSpan at)
        => Request(context, unit, gate, at, out _);

    private CommandGateCheckResult Request(Context context, Unit unit, CommandGateManager gate, TimeSpan at,
        out Guid commandId)
    {
        commandId = Guid.NewGuid();
        CommandEvent command = context.Factory.NewEvent<CommandEvent>() with
        {
            AbilityId = "test",
            Source = unit,
            Receiver = unit.EventReceiver,
        };
        context.Dispatch(context.Factory.NewEvent<UnitActionGateRequestEvent>() with
        {
            Source = unit,
            Origin = command,
            CommandId = commandId,
            ActivationId = commandId,
            AbilityId = command.AbilityId,
            AbilityGate = gate,
            OccupiesChannel = gate.OccupiesWindup,
            ActionCancellation = new CancellationTokenSource(),
            Receiver = unit.EventReceiver,
            TimeSpan = at - context.ContextTimeSpan,
        }).ToList();
        context.AdvanceTo(at);
        Guid requestedId = commandId;
        CommandGateCheckResult result = context.History.OfType<UnitActionGateResultEvent>()
            .Single(e => e.Request.CommandId == requestedId).Result;
        TestContext.WriteLine($"time={at}, accepted={result.CanExecute}, reason={result.FailureReason}, next={result.NextAvailableTime}");
        return result;
    }

    private static (Context Context, Unit Unit) CreateUnit(TriggerChainFactory? factory = null)
    {
        Context context = new(causalityFactory: factory);
        Unit unit = CreateAdditionalUnit(context);
        return (context, unit);
    }

    private static Unit CreateAdditionalUnit(Context context)
    {
        Unit unit = new()
        {
            Name = "test",
            ArchetypeId = "test",
            Property = new UnitProperty { LifePoint = 100, LifePointMax = 100 },
            Abilities = new UnitAbilityManager(),
            EventReceiver = context.Factory.NewEventReceiver(),
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler(), new AbilityLifecycleHandler());
        context.AddEventReceiver(unit.EventReceiver);
        unit.Initialize();
        return unit;
    }
}
