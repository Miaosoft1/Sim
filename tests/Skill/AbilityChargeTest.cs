using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class AbilityChargeTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void InvalidConfigurationsAreRejectedAndUnconfiguredChargesAreUnlimited()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AbilityChargeConfiguration(0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AbilityChargeConfiguration(2, 3));
        Assert.ThrowsExactly<ArgumentException>(() => new AbilityChargeConfiguration(2, 1, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AbilityChargeConfiguration(2, 1, TimeSpan.Zero, AbilityChargeRecoveryMode.Serial));
        var gate = new CommandGateManager { OccupiesWindup = false };
        Assert.IsNull(gate.GetChargeSnapshot());
        Assert.IsTrue(gate.Check(TimeSpan.Zero, null).CanExecute);
        TestContext.WriteLine("Invalid finite recovery configurations were rejected; an unconfigured gate remained unlimited.");
    }

    [TestMethod]
    public void InitialMissingChargesRecoverSeriallyFromContextEntry()
    {
        using var battle = new Fixture(new(3, 0, TimeSpan.FromSeconds(2), AbilityChargeRecoveryMode.Serial));
        Assert.AreEqual(0, battle.Snapshot().Available);
        Assert.AreEqual(TimeSpan.FromSeconds(2), battle.Snapshot().Recoveries.Single().At);
        battle.Advance(1);
        Assert.AreEqual(0, battle.Snapshot().Available);
        battle.Advance(2);
        Assert.AreEqual(1, battle.Snapshot().Available);
        Assert.AreEqual(TimeSpan.FromSeconds(4), battle.Snapshot().Recoveries.Single().At);
        battle.Advance(6);
        Assert.AreEqual(3, battle.Snapshot().Available);
        Assert.IsEmpty(battle.Snapshot().Recoveries);
        int[] notifications = battle.Context.History.OfType<AbilityChargesChangedEvent>()
            .OrderBy(ev => ev.Execute).Select(ev => ev.Charges.Available).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, notifications);
        TestContext.WriteLine("An initial 0/3 charge state recovered at simulation seconds 2, 4 and 6.");
    }

    [TestMethod]
    public void InitialMissingChargesRecoverInParallel()
    {
        using var battle = new Fixture(new(3, 0, TimeSpan.FromSeconds(2), AbilityChargeRecoveryMode.Parallel));
        AbilityChargeRecoveryDeadline initial = battle.Snapshot().Recoveries.Single();
        Assert.AreEqual(TimeSpan.FromSeconds(2), initial.At);
        Assert.AreEqual(3, initial.Count);
        battle.Advance(2);
        Assert.AreEqual(3, battle.Snapshot().Available);
        Assert.IsEmpty(battle.Snapshot().Recoveries);
        TestContext.WriteLine("All three initial parallel recovery timers started together when the ability entered the context.");
    }

    [TestMethod]
    public async Task RealtimePauseDoesNotAdvanceChargeRecovery()
    {
        var clock = new Miaosoft.Sim.Tests.Support.ManualTimeProvider();
        using var context = new Context(realtime: true, timeScale: 0, timeProvider: clock,
            causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        var gate = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 0, TimeSpan.FromSeconds(2), AbilityChargeRecoveryMode.Serial)
        };
        var ability = new ProjectileAbility { Id = "paused", GateManager = gate };
        gate.EnterContext(context, ability);
        while (context.HasEventsDueNow) await context.NextAsync();
        Task<ContextStepResult> paused = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        clock.Advance(TimeSpan.FromHours(1));
        context.Inbox.Signal();
        await paused.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);

        context.Inbox.RequestTimeScale(2);
        for (int attempts = 0; context.TimeScale != 2 && attempts < 3; attempts++)
            await context.NextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2d, context.TimeScale);
        Task<ContextStepResult> resumed = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsInstanceOfType<AbilityChargeTimerEvent>((await resumed.WaitAsync(TimeSpan.FromSeconds(5))).Event);
        Assert.AreEqual(TimeSpan.FromSeconds(2), context.ContextTimeSpan);
        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("One wall-clock hour while paused did not restore charges; one second at 2x restored at two simulation seconds.");
    }

    [TestMethod]
    public void ClearingPendingEventsDoesNotRescheduleChargeTimers()
    {
        using var battle = new Fixture(new(2, 0, TimeSpan.FromSeconds(2), AbilityChargeRecoveryMode.Serial));
        Assert.IsTrue(battle.Context.HasPendingEvents);

        battle.Context.ClearPendingEvents();

        Assert.IsFalse(battle.Context.HasPendingEvents);
        Assert.AreEqual(0, battle.Snapshot().Available);
        battle.Gate.EnterContext(battle.Context, battle.Ability);
        battle.Advance(0);
        Assert.IsFalse(battle.Context.HasPendingEvents);
        Assert.AreEqual(CommandExecutionFailureReason.ChargesDepleted,
            battle.Gate.Check(battle.Context.ContextTimeSpan, null).FailureReason);
        Assert.IsTrue(battle.Context.HasPendingEvents);
        battle.Advance(2);
        Assert.AreEqual(1, battle.Snapshot().Available);
        TestContext.WriteLine("Queue clearing and a holder change did not revive combat timers; a new activation check explicitly rearmed recovery.");
    }

    [TestMethod]
    public void SerialConditionalRecoveryPreservesProgressAndInvalidatesOldTimersWhenFull()
    {
        using var battle = new Fixture(new(3, 0, TimeSpan.FromSeconds(10), AbilityChargeRecoveryMode.Serial));
        battle.Advance(3);
        Assert.AreEqual(1, battle.Restore(1).Restored);
        Assert.AreEqual(TimeSpan.FromSeconds(10), battle.Snapshot().Recoveries.Single().At);
        battle.Advance(10);
        Assert.AreEqual(2, battle.Snapshot().Available);
        battle.Advance(12);
        Assert.AreEqual(1, battle.Restore(10).Restored);
        Assert.IsEmpty(battle.Snapshot().Recoveries);
        battle.Advance(15);
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Use());
        Assert.AreEqual(TimeSpan.FromSeconds(25), battle.Snapshot().Recoveries.Single().At);
        battle.Advance(20);
        Assert.AreEqual(2, battle.Snapshot().Available);
        battle.Advance(25);
        Assert.AreEqual(3, battle.Snapshot().Available);
        TestContext.WriteLine("Serial conditional recovery retained the first timer and an obsolete timer did not fill a later consumption.");
    }

    [TestMethod]
    public void ParallelConditionalRecoveryCancelsTheLatestTimer()
    {
        using var battle = new Fixture(new(3, 3, TimeSpan.FromSeconds(10), AbilityChargeRecoveryMode.Parallel));
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Use());
        battle.Advance(2);
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Use());
        battle.Advance(4);
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Use());
        battle.Advance(5);
        Assert.AreEqual(1, battle.Restore(1).Restored);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12) },
            battle.Snapshot().Recoveries.Select(timer => timer.At).ToArray());
        battle.Advance(12);
        Assert.AreEqual(3, battle.Snapshot().Available);
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Use());
        battle.Advance(14);
        Assert.AreEqual(2, battle.Snapshot().Available);
        battle.Advance(22);
        Assert.AreEqual(3, battle.Snapshot().Available);
        TestContext.WriteLine("Parallel conditional recovery removed the latest timer; its stale event could not refill a later use.");
    }

    [TestMethod]
    public void SignalRestoresOnlyTheBoundAbilityWithoutActivatingIt()
    {
        using var battle = new Fixture(new(3, 0, TimeSpan.FromSeconds(10), AbilityChargeRecoveryMode.Serial));
        TriggerHandler trigger = battle.InstallRecoveryTrigger(2);
        battle.Advance(3);

        battle.Signal("other");
        Assert.AreEqual(0, battle.Snapshot().Available);
        battle.Signal("recharge");

        Assert.AreEqual(2, battle.Snapshot().Available);
        Assert.AreEqual(TimeSpan.FromSeconds(10), battle.Snapshot().Recoveries.Single().At);
        AbilityChargeRestoreResultEvent result = battle.Owner.EventReceiver.History
            .OfType<AbilityChargeRestoreResultEvent>().Single();
        Assert.AreEqual(2, result.Restored);
        Assert.AreEqual(battle.Ability.RuntimeInstanceId, result.AbilityInstanceId);
        Assert.HasCount(1, battle.Owner.EventReceiver.History.OfType<AbilityChargeRestoreInvokeEvent>());
        Assert.IsEmpty(battle.Owner.EventReceiver.History.OfType<ProjectileEvent>());
        Assert.AreEqual(battle.Ability.RuntimeInstanceId, trigger.TargetInstanceId);
        battle.Advance(10);
        Assert.AreEqual(3, battle.Snapshot().Available);
        TestContext.WriteLine("A matching signal restored the bound ability twice without activating it or resetting its existing timer.");
    }

    [TestMethod]
    public void RemovedRecoveryRegistrationCannotRestoreQueuedInvocation()
    {
        using var battle = new Fixture(new(2, 0));
        TriggerHandler trigger = battle.InstallRecoveryTrigger(1);
        battle.Context.EventProcessed += ev =>
        {
            if (ev is AbilityChargeRestoreInvokeEvent)
                battle.Context.Dispatch(battle.Context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
                {
                    CommandEventId = Guid.NewGuid(), Source = battle.Owner, Target = battle.Owner,
                    AbilityIds = [trigger.Id], Receiver = battle.Owner.EventReceiver, Priority = -100
                }).ToList();
        };

        battle.Signal("recharge");

        Assert.AreEqual(0, battle.Snapshot().Available);
        Assert.IsFalse(battle.Owner.EventReceiver.GetHandlers<TriggerHandler>().Contains(trigger));
        Assert.AreEqual(AbilityChargeRestoreFailureReason.AbilityNotAvailable,
            battle.Owner.EventReceiver.History.OfType<AbilityChargeRestoreResultEvent>().Single().FailureReason);
        TestContext.WriteLine("Removing the triggering registration before a queued restoration prevented its execution.");
    }

    [TestMethod]
    public void RecoveryDoesNotObserveEventsWhileItsSourceIsUnauthorized()
    {
        using var battle = new Fixture(new(2, 0), bound: true);
        TriggerHandler trigger = battle.InstallRecoveryTrigger(1);
        var revoked = new AtomicTransaction();
        battle.Ability.Binding.StageAuthority(revoked, battle.Context, null);
        trigger.Binding.StageAuthority(revoked, battle.Context, null);
        revoked.Commit();

        battle.Signal("recharge");
        Assert.AreEqual(0, battle.Snapshot().Available);
        Assert.IsEmpty(battle.Owner.EventReceiver.History.OfType<AbilityChargeRestoreInvokeEvent>());

        var restored = new AtomicTransaction();
        battle.Ability.Binding.StageAuthority(restored, battle.Context, battle.Owner.Id);
        trigger.Binding.StageAuthority(restored, battle.Context, battle.Owner.Id);
        restored.Commit();
        battle.Signal("recharge");
        Assert.AreEqual(1, battle.Snapshot().Available);
        TestContext.WriteLine("An unauthorized source did not observe a signal; renewed authorization restored the same instance.");
    }

    [TestMethod]
    public void QueuedRecoveryCannotCrossAnAuthorityChangeAndReturn()
    {
        using var battle = new Fixture(new(2, 0), bound: true);
        TriggerHandler trigger = battle.InstallRecoveryTrigger(1);
        battle.Context.EventProcessed += ev =>
        {
            if (ev is not AbilityChargeRestoreRequestEvent) return;
            var revoke = new AtomicTransaction();
            battle.Ability.Binding.StageAuthority(revoke, battle.Context, null);
            trigger.Binding.StageAuthority(revoke, battle.Context, null);
            revoke.Commit();
            var restore = new AtomicTransaction();
            battle.Ability.Binding.StageAuthority(restore, battle.Context, battle.Owner.Id);
            trigger.Binding.StageAuthority(restore, battle.Context, battle.Owner.Id);
            restore.Commit();
        };

        battle.Signal("recharge");

        Assert.AreEqual(0, battle.Snapshot().Available);
        Assert.AreEqual(AbilityChargeRestoreFailureReason.AbilityNotAvailable,
            battle.Owner.EventReceiver.History.OfType<AbilityChargeRestoreResultEvent>().Single().FailureReason);
        TestContext.WriteLine("A queued restoration remained invalid after its source lost and regained the same owner.");
    }

    [TestMethod]
    public void RecoveryRejectsAnInconsistentTriggerSource()
    {
        using var battle = new Fixture(new(2, 0));
        TriggerHandler trigger = battle.InstallRecoveryTrigger(1);
        var binding = new AtomicTransaction();
        trigger.Binding.StageBind(binding, new TestAbilitySource(Guid.NewGuid()));
        trigger.Binding.StageAuthority(binding, battle.Context, battle.Owner.Id);
        binding.Commit();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => battle.Signal("recharge"));
        StringAssert.Contains(error.Message, "share a source and identity");
        Assert.AreEqual(0, battle.Snapshot().Available);
        TestContext.WriteLine("A trigger bound to a different source failed explicitly without restoring charges.");
    }

    [TestMethod]
    public void ConcurrentActivationsCompeteForTheLastChargeWithoutDoubleSpending()
    {
        using var battle = new Fixture(new(2, 1), cost: 10);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Assert.IsTrue(battle.Gate.ValidateAndReserve(TimeSpan.Zero, null, first).CanExecute);
        Assert.IsTrue(battle.Gate.ValidateAndReserve(TimeSpan.Zero, null, second).CanExecute);
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Gate.CommitRelease(battle.Owner, first, TimeSpan.Zero));
        Assert.AreEqual(CommandReleaseResult.ChargesDepleted, battle.Gate.CommitRelease(battle.Owner, second, TimeSpan.Zero));
        Assert.AreEqual(0, battle.Snapshot().Available);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        Assert.IsFalse(battle.Gate.HasPendingReservation);
        TestContext.WriteLine("Two accepted activations competed for one charge; only one spent energy and committed.");
    }

    [TestMethod]
    public void FailedResourceAndCanceledActivationsDoNotConsumeChargesOrStartRecovery()
    {
        using var battle = new Fixture(
            new(2, 2, TimeSpan.FromSeconds(10), AbilityChargeRecoveryMode.Serial), cost: 60);
        Assert.AreEqual(CommandReleaseResult.ResourceInsufficient, battle.Use());
        Assert.AreEqual(2, battle.Snapshot().Available);
        Assert.IsEmpty(battle.Snapshot().Recoveries);
        Assert.AreEqual(TimeSpan.Zero, battle.Gate.NextReadyTime);
        using var cancellation = new CancellationTokenSource();
        Guid activation = Guid.NewGuid();
        Assert.IsTrue(battle.Gate.ValidateAndReserve(TimeSpan.Zero, null, activation, cancellation.Token).CanExecute);
        cancellation.Cancel();
        Assert.AreEqual(CommandReleaseResult.Canceled,
            battle.Gate.CommitRelease(battle.Owner, activation, TimeSpan.Zero, cancellation.Token));
        Assert.AreEqual(2, battle.Snapshot().Available);
        Assert.IsEmpty(battle.Snapshot().Recoveries);
        Assert.AreEqual(50m, battle.Owner.Property.Energy);
        TestContext.WriteLine("Insufficient energy and cancellation left the finite charge state, recovery and cooldown unchanged.");
    }

    [TestMethod]
    public void OrdinaryCooldownAndChargeRecoveryRemainIndependent()
    {
        using var battle = new Fixture(
            new(2, 2, TimeSpan.FromSeconds(2), AbilityChargeRecoveryMode.Serial),
            cost: 10, cooldown: TimeSpan.FromSeconds(5));
        Assert.AreEqual(CommandReleaseResult.Committed, battle.Use());
        Assert.AreEqual(CommandExecutionFailureReason.CoolingDown,
            battle.Gate.Check(TimeSpan.Zero, null).FailureReason);
        battle.Advance(2);
        Assert.AreEqual(2, battle.Snapshot().Available);
        Assert.AreEqual(CommandExecutionFailureReason.CoolingDown,
            battle.Gate.Check(battle.Context.ContextTimeSpan, null).FailureReason);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        battle.Advance(5);
        Assert.IsTrue(battle.Gate.Check(battle.Context.ContextTimeSpan, null).CanExecute);
        TestContext.WriteLine("A charge recovered at 2s while the separately configured 5s casting cooldown remained active.");
    }

    [TestMethod]
    public void UnheldAbilityRecoversButCannotRestoreByConditionOrCrossContexts()
    {
        using var battle = new Fixture(new(3, 0, TimeSpan.FromSeconds(2), AbilityChargeRecoveryMode.Serial),
            install: false, bound: true);
        Assert.AreEqual(AbilityChargeRestoreFailureReason.AbilityNotAvailable, battle.Restore(1).FailureReason);
        battle.Advance(2);
        Assert.AreEqual(1, battle.Snapshot().Available);
        Guid attempt = Guid.NewGuid();
        Assert.IsTrue(battle.Gate.ValidateAndReserve(battle.Context.ContextTimeSpan, null, attempt).CanExecute);
        Assert.AreEqual(CommandReleaseResult.AbilityNotAvailable,
            battle.Gate.CommitRelease(battle.Owner, attempt, battle.Context.ContextTimeSpan));
        Assert.AreEqual(1, battle.Snapshot().Available);

        using var other = new Context();
        Assert.ThrowsExactly<InvalidOperationException>(() => battle.Gate.EnterContext(other, battle.Ability));
        battle.AuthorizeAndInstall();
        Assert.AreEqual(1, battle.Snapshot().Available);
        battle.Advance(4);
        Assert.AreEqual(2, battle.Snapshot().Available);
        Assert.AreEqual(1, battle.Restore(1).Restored);
        Assert.AreEqual(3, battle.Snapshot().Available);
        TestContext.WriteLine("An unheld instance kept its simulation timer, rejected condition recovery and could not enter a second context.");
    }

    [TestMethod]
    public void TriggeredHealConsumesItsOwnChargeAndResourceOnlyOnSuccess()
    {
        using var battle = new HealFixture();

        battle.Hit();
        battle.Hit();

        Assert.AreEqual(57, battle.Owner.Property.LifePoint);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        Assert.AreEqual(0, battle.Gate.GetChargeSnapshot()!.Available);
        CollectionAssert.AreEqual(new[] { 7 }, battle.Owner.EventReceiver.History
            .OfType<HealLifeGainEvent>().Select(ev => ev.LifeGain).ToArray());
        Assert.HasCount(1, battle.Owner.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.AbilityInstanceId == battle.Ability.RuntimeInstanceId &&
                ev.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        TestContext.WriteLine("A hit-triggered heal spent one charge and 10 energy once; the next activation failed explicitly.");
    }

    [TestMethod]
    public void CommandHealUsesTheSameGateAndReportsTheCommandResult()
    {
        using var battle = new HealFixture();

        CommandEvent first = battle.Cast();
        CommandEvent second = battle.Cast();

        Assert.AreEqual(57, battle.Owner.Property.LifePoint);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        Assert.AreEqual(CommandExecutionStatus.Success, battle.Result(first).Status);
        Assert.AreEqual(CommandExecutionFailureReason.ChargesDepleted, battle.Result(second).FailureReason);
        Assert.AreEqual(0, battle.Gate.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("Direct commands used the healing instance's gate and returned distinct success and charge-failure results.");
    }

    [TestMethod]
    public void ObservingAnotherCommandProducesAnAbilityResultInsteadOfACommandResult()
    {
        using var battle = new HealFixture();
        battle.InstallCommandObserver();

        CommandEvent observed = battle.ObserveOwnCommand();

        Assert.AreEqual(57, battle.Owner.Property.LifePoint);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        Assert.AreEqual(0, battle.Gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(battle.Owner.EventReceiver.History.OfType<CommandExecutionEvent>()
            .Where(result => ReferenceEquals(result.Command, observed)));
        AbilityActivationResultEvent result = battle.Owner.EventReceiver.History
            .OfType<AbilityActivationResultEvent>().Single();
        Assert.AreEqual(battle.Ability.RuntimeInstanceId, result.AbilityInstanceId);
        Assert.AreEqual(CommandExecutionStatus.Success, result.Status);
        TestContext.WriteLine("An ability observing another command produced its own activation result, not a result for the observed command.");
    }

    [TestMethod]
    public void RespondingToAnotherUnitsCommandUsesTheRespondingOwnerAndTargets()
    {
        using var battle = new HealFixture();

        CommandEvent observed = battle.ObserveOtherCommand();

        Assert.AreEqual(57, battle.Owner.Property.LifePoint);
        Assert.AreEqual(50, battle.Other.Property.LifePoint);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        Assert.AreEqual(50m, battle.Other.Property.Energy);
        Assert.AreEqual(0, battle.Gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(battle.Context.History.OfType<CommandExecutionEvent>()
            .Where(result => ReferenceEquals(result.Command, observed)));
        AbilityActivationResultEvent result = battle.Owner.EventReceiver.History
            .OfType<AbilityActivationResultEvent>().Single();
        Assert.AreSame(battle.Owner, result.Source);
        Assert.AreEqual(CommandExecutionStatus.Success, result.Status);
        TestContext.WriteLine("A response to another unit's command used the response owner, target, resource and result identity.");
    }

    [TestMethod]
    public void AGloballyObservedCommandUsesTheRespondingOwnersGateAndSelector()
    {
        using var battle = new HealFixture();
        var map = new MapRuntime();
        foreach ((Unit unit, Vector2D position) in new[]
        {
            (battle.Owner, new Vector2D(0, 0)), (battle.Other, new Vector2D(10, 0))
        })
        {
            var body = new MapObject(unit.Id.ToString("N"), position, unit.EventReceiver);
            map.AddObject(body);
            unit.MapObjects.Initialize(body);
        }
        battle.InstallCommandObserver(TriggerSourceScope.Any);

        CommandEvent observed = battle.DispatchOtherCommand();

        Assert.AreEqual(57, battle.Owner.Property.LifePoint);
        Assert.AreEqual(50, battle.Other.Property.LifePoint);
        Assert.AreEqual(40m, battle.Owner.Property.Energy);
        Assert.AreEqual(50m, battle.Other.Property.Energy);
        Assert.AreEqual(0, battle.Gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(battle.Context.History.OfType<CommandExecutionEvent>()
            .Where(result => ReferenceEquals(result.Command, observed)));
        AbilityActivationResultEvent result = battle.Owner.EventReceiver.History
            .OfType<AbilityActivationResultEvent>().Single();
        Assert.AreSame(battle.Owner, result.Source);
        Assert.AreEqual(CommandExecutionStatus.Success, result.Status);
        AbilityTriggerInvokeEvent invocation = battle.Owner.EventReceiver.History.OfType<AbilityTriggerInvokeEvent>()
            .Single(ev => ReferenceEquals(ev.UpstreamEvent, observed));
        Assert.AreSame(battle.Owner.MapObject, invocation.SourceObject);
        TestContext.WriteLine("A published command reached another unit's listener and consumed only the responder's resource and charge.");
    }

    [TestMethod]
    public void AForeignCommandCannotBeClaimedAsADirectActivation()
    {
        using var battle = new HealFixture();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => battle.ObserveOtherCommand(isDirect: true));

        StringAssert.Contains(error.Message, "direct command");
        Assert.AreEqual(50, battle.Owner.Property.LifePoint);
        Assert.AreEqual(50, battle.Other.Property.LifePoint);
        Assert.AreEqual(50m, battle.Owner.Property.Energy);
        Assert.AreEqual(1, battle.Gate.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("A command from another owner could not be mislabeled as this ability's direct command.");
    }

    [TestMethod]
    public void TriggeredHealRechecksAuthorityAtReleaseAfterWindup()
    {
        using var battle = new HealFixture(TimeSpan.FromSeconds(2), bound: true);

        battle.Hit();
        var transaction = new AtomicTransaction();
        battle.Ability.Binding.StageAuthority(transaction, battle.Context, battle.Other.Id);
        transaction.Commit();
        battle.Advance(2);

        Assert.AreEqual(50, battle.Owner.Property.LifePoint);
        Assert.AreEqual(50m, battle.Owner.Property.Energy);
        Assert.AreEqual(1, battle.Gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(battle.Owner.EventReceiver.History.OfType<HealLifeGainEvent>());
        Assert.HasCount(1, battle.Owner.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.AbilityInstanceId == battle.Ability.RuntimeInstanceId &&
                ev.FailureReason == CommandExecutionFailureReason.AbilityNotAvailable));
        TestContext.WriteLine("Changing the bound owner during windup rejected the heal without spending its charge or energy.");
    }

    private static async Task WaitForTimer(Miaosoft.Sim.Tests.Support.ManualTimeProvider clock)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (clock.PendingTimerCount == 0) await Task.Delay(1, timeout.Token);
    }

    [TestMethod]
    public void RuntimeAddressedCommandsSelectOneOfTwoSameDefinitionInstancesWithoutFallback()
    {
        using var battle = new HealFixture();
        HealAbility[] choices = InstallCommandChoices(battle.Context, battle.Owner);
        Assert.HasCount(2, battle.Owner.EventReceiver.GetHandlers<HealAbility>().Where(a => a.Id == "choice"));

        CommandEvent first = DispatchChoice(battle.Context, battle.Owner, choices[0].RuntimeInstanceId);
        Assert.AreEqual(53, battle.Owner.Property.LifePoint);
        Assert.AreEqual(0, choices[0].GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(1, choices[1].GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(CommandExecutionStatus.Success, battle.Result(first).Status);

        CommandEvent exhausted = DispatchChoice(battle.Context, battle.Owner, choices[0].RuntimeInstanceId);
        Assert.AreEqual(CommandExecutionFailureReason.ChargesDepleted, battle.Result(exhausted).FailureReason);
        Assert.AreEqual(53, battle.Owner.Property.LifePoint);
        Assert.AreEqual(1, choices[1].GateManager!.GetChargeSnapshot()!.Available);

        CommandEvent second = DispatchChoice(battle.Context, battle.Owner, choices[1].RuntimeInstanceId);
        Assert.AreEqual(CommandExecutionStatus.Success, battle.Result(second).Status);
        Assert.AreEqual(60, battle.Owner.Property.LifePoint);
        Assert.AreEqual(0, choices[1].GateManager!.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("Two same-definition abilities healed 3 and 7 independently; an exhausted selected instance did not fall back to the other.");
    }

    [TestMethod]
    public void AmbiguousForeignAndUnauthorizedCommandInstancesFailBeforeActivation()
    {
        using var battle = new HealFixture();
        HealAbility[] choices = InstallCommandChoices(battle.Context, battle.Owner);
        HealAbility[] foreign = InstallCommandChoices(battle.Context, battle.Other);
        CommandEvent ambiguous = DispatchChoice(battle.Context, battle.Owner, null);
        Assert.AreEqual(CommandExecutionFailureReason.AmbiguousAbility, battle.Result(ambiguous).FailureReason);
        foreach (Guid invalid in new[] { Guid.Empty, Guid.NewGuid(), foreign[0].RuntimeInstanceId, battle.Ability.RuntimeInstanceId })
        {
            CommandEvent command = DispatchChoice(battle.Context, battle.Owner, invalid);
            Assert.AreEqual(CommandExecutionFailureReason.AbilityNotAvailable, battle.Result(command).FailureReason);
        }
        var revoke = new AtomicTransaction();
        choices[0].Binding.StageAuthority(revoke, battle.Context, battle.Other.Id);
        revoke.Commit();
        CommandEvent unauthorized = DispatchChoice(battle.Context, battle.Owner, choices[0].RuntimeInstanceId);
        Assert.AreEqual(CommandExecutionFailureReason.AbilityNotAvailable, battle.Result(unauthorized).FailureReason);
        Assert.AreEqual(50, battle.Owner.Property.LifePoint);
        Assert.IsTrue(choices.Concat(foreign).All(a => a.GateManager!.GetChargeSnapshot()!.Available == 1));
        Assert.IsEmpty(battle.Context.History.OfType<AbilityTriggerInvokeEvent>()
            .Where(invocation => invocation.AbilityId == "choice"));
        TestContext.WriteLine("Missing selection, empty/unknown/foreign IDs, wrong definitions and revoked authority failed without activating or consuming any instance.");
    }

    [TestMethod]
    public void ADirectInvocationCannotSubstituteAnotherRuntimeInstance()
    {
        using var battle = new HealFixture();
        HealAbility[] choices = InstallCommandChoices(battle.Context, battle.Owner);
        var command = battle.Context.Factory.NewEvent<CommandEvent>() with
        {
            AbilityId = "choice", AbilityInstanceId = choices[0].RuntimeInstanceId,
            Source = battle.Owner, Target = battle.Owner, Receiver = battle.Owner.EventReceiver
        };
        var invocation = battle.Context.Factory.NewEvent<AbilityTriggerInvokeEvent>() with
        {
            AbilityId = "choice", TargetRuntimeInstanceId = choices[1].RuntimeInstanceId,
            CommandEventId = command.CommandInstanceId, UpstreamEvent = command, IsDirectCommand = true,
            Source = battle.Owner, Target = battle.Owner, Receiver = battle.Owner.EventReceiver
        };
        battle.Context.Dispatch(invocation).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => battle.Advance(0));
        Assert.AreEqual(50, battle.Owner.Property.LifePoint);
        Assert.IsTrue(choices.All(a => a.GateManager!.GetChargeSnapshot()!.Available == 1));
        TestContext.WriteLine("A forged direct invocation could not substitute the unselected runtime instance.");
    }

    private static HealAbility[] InstallCommandChoices(Context context, Unit owner)
    {
        var choices = new[] { 3, 7 }.Select(amount => new HealAbility
        {
            Id = "choice", BaseHeal = amount,
            GateManager = new CommandGateManager
            { Charges = new AbilityChargeConfiguration(1, 1), OccupiesWindup = false }
        }).ToArray();
        var handlers = new List<IHandler>();
        var transaction = new AtomicTransaction();
        foreach (HealAbility choice in choices)
        {
            choice.Binding.StageBind(transaction, new TestAbilitySource(Guid.NewGuid()));
            choice.Binding.StageAuthority(transaction, context, owner.Id);
            var trigger = new TriggerHandler
            {
                Id = $"choice.{choice.RuntimeInstanceId:N}", TargetAbilityId = choice.Id,
                TriggerType = TriggerType.Command
            };
            trigger.StageBindTarget(transaction, choice.RuntimeInstanceId);
            handlers.Add(choice);
            handlers.Add(trigger);
        }
        transaction.Commit();
        context.Dispatch(context.Factory.NewEvent<AbilityInstallEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
            Handlers = handlers, Receiver = owner.EventReceiver
        }).ToList();
        Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
        return choices;
    }

    private static CommandEvent DispatchChoice(Context context, Unit owner, Guid? instanceId)
    {
        var command = context.Factory.NewEvent<CommandEvent>() with
        {
            AbilityId = "choice", AbilityInstanceId = instanceId,
            Source = owner, Target = owner, Receiver = owner.EventReceiver
        };
        context.Dispatch(command).ToList();
        Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
        return command;
    }

    private sealed class HealFixture : IDisposable
    {
        public HealFixture(TimeSpan? windup = null, bool bound = false)
        {
            Context = new Context(causalityFactory: new TriggerChainFactory());
            Context.Logger.Output = null;
            Owner = CreateUnit("owner");
            Other = CreateUnit("other");
            Gate = new CommandGateManager
            {
                Charges = new AbilityChargeConfiguration(1, 1),
                ResourceCost = 10,
                Windup = windup,
                OccupiesWindup = false
            };
            Ability = new HealAbility { Id = "heal", BaseHeal = 7, GateManager = Gate, Windup = windup };
            if (bound)
            {
                var transaction = new AtomicTransaction();
                Ability.Binding.StageBind(transaction, new TestAbilitySource(Guid.NewGuid()));
                Ability.Binding.StageAuthority(transaction, Context, Owner.Id);
                transaction.Commit();
            }
            var hit = new TriggerHandler
            {
                Id = "heal.hit", TargetAbilityId = Ability.Id, TriggerType = TriggerType.ProjectileHit,
                SourceAbilityIds = ["delivery"]
            };
            var command = new TriggerHandler
            {
                Id = "heal.command", TargetAbilityId = Ability.Id, TriggerType = TriggerType.Command
            };
            TestHandlerFactory.Register(Owner, Ability, hit, command);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = Owner, Target = Owner,
                AbilityIds = [Ability.Id, hit.Id, command.Id], Receiver = Owner.EventReceiver
            }).ToList();
            Advance(0);
        }

        public Context Context { get; }
        public Unit Owner { get; }
        public Unit Other { get; }
        public CommandGateManager Gate { get; }
        public HealAbility Ability { get; }

        public void Hit()
        {
            Context.Dispatch(ProjectileHitPublicationEvent.FromHit(Context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = Owner, Target = Owner,
                Receiver = Owner.EventReceiver
            })).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
        }

        public CommandEvent Cast()
        {
            var command = Context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = Ability.Id, Source = Owner, Target = Owner, Receiver = Owner.EventReceiver
            };
            Context.Dispatch(command).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
            return command;
        }

        public void InstallCommandObserver(TriggerSourceScope scope = TriggerSourceScope.Self)
        {
            var trigger = new TriggerHandler
            {
                Id = "heal.command-observer", TargetAbilityId = Ability.Id,
                Config = new AbilityTriggerConfig
                {
                    Type = TriggerType.Command,
                    SourceAbilityIds = ["observed-command"],
                    EventSourceScope = scope
                }
            };
            TestHandlerFactory.Register(Owner, trigger);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = Owner, Target = Owner,
                AbilityIds = [trigger.Id], Receiver = Owner.EventReceiver
            }).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
        }

        public CommandEvent ObserveOwnCommand()
        {
            InstallObservedCommand(Owner);
            var command = Context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "observed-command", Source = Owner, Target = Owner, Receiver = Owner.EventReceiver
            };
            Context.Dispatch(command).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
            return command;
        }

        public CommandEvent ObserveOtherCommand(bool isDirect = false)
        {
            CommandEvent command = PrepareOtherCommand();
            Context.Dispatch(Context.Factory.NewEvent<AbilityTriggerInvokeEvent>() with
            {
                CommandEventId = command.CommandInstanceId, AbilityId = Ability.Id,
                TargetRuntimeInstanceId = Ability.RuntimeInstanceId, UpstreamEvent = command,
                Source = Owner, Target = Owner, Receiver = Owner.EventReceiver, IsDirectCommand = isDirect
            }).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
            return command;
        }

        public CommandEvent DispatchOtherCommand()
        {
            InstallObservedCommand(Other);
            CommandEvent command = PrepareOtherCommand();
            Context.Dispatch(command).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
            return command;
        }

        private void InstallObservedCommand(Unit owner)
        {
            var ability = new TestCommandAbility { Id = "observed-command" };
            TestHandlerFactory.Register(owner, ability);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                AbilityIds = [ability.Id], Receiver = owner.EventReceiver
            }).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
        }

        private CommandEvent PrepareOtherCommand()
        {
            var registry = UnitSpatialRegistry.ForContext(Context);
            registry.Register(Owner);
            registry.Register(Other);
            Owner.Registry = registry;
            Other.Registry = registry;
            Ability.Selector = new TrackingSelector { Origin = TargetingOrigin.Source, Filter = TargetFilter.Self };
            return Context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "observed-command", Source = Other, Target = Other, Receiver = Other.EventReceiver
            };
        }

        public CommandExecutionEvent Result(CommandEvent command) =>
            Owner.EventReceiver.History.OfType<CommandExecutionEvent>()
                .Single(result => ReferenceEquals(result.Command, command));

        public void Advance(double seconds) => Context.AdvanceTo(TimeSpan.FromSeconds(seconds));

        public void Dispose() => Context.Dispose();

        private Unit CreateUnit(string name)
        {
            var unit = new Unit
            {
                Name = name, ArchetypeId = name,
                Property = new UnitProperty
                {
                    LifePoint = 50, LifePointMax = 100, EnergyMax = 100, Energy = 50,
                    EnergyRecoveryPerSecond = 0
                },
                Abilities = new UnitAbilityManager(), EventReceiver = Context.Factory.NewEventReceiver()
            };
            unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler());
            Context.AddEventReceiver(unit.EventReceiver);
            unit.Initialize();
            return unit;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(AbilityChargeConfiguration configuration, int cost = 0, bool install = true, bool bound = false,
            TimeSpan? cooldown = null)
        {
            Context = new Context(causalityFactory: new TriggerChainFactory());
            Context.Logger.Output = null;
            Owner = new Unit
            {
                Name = "owner", ArchetypeId = "owner",
                Property = new UnitProperty
                {
                    LifePoint = 100, LifePointMax = 100, EnergyMax = 100, Energy = 50, EnergyRecoveryPerSecond = 0
                },
                Abilities = new UnitAbilityManager(), EventReceiver = Context.Factory.NewEventReceiver()
            };
            Owner.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler());
            Context.AddEventReceiver(Owner.EventReceiver);
            Owner.Initialize();
            Gate = new CommandGateManager
            { OccupiesWindup = false, Charges = configuration, ResourceCost = cost, Cooldown = cooldown };
            Ability = new ProjectileAbility { Id = "charged", GateManager = Gate };
            if (bound)
            {
                var transaction = new AtomicTransaction();
                Ability.Binding.StageBind(transaction, new TestAbilitySource(Guid.NewGuid()));
                Ability.Binding.StageAuthority(transaction, Context, null);
                transaction.Commit();
            }
            if (install) AuthorizeAndInstall();
            else Gate.EnterContext(Context, Ability);
            Advance(0);
        }

        public Context Context { get; }
        public Unit Owner { get; }
        public CommandGateManager Gate { get; }
        public ProjectileAbility Ability { get; }
        public AbilityChargeSnapshot Snapshot() => Gate.GetChargeSnapshot()!;

        public TriggerHandler InstallRecoveryTrigger(int amount)
        {
            var trigger = new TriggerHandler
            {
                Id = "recovery-trigger", TargetAbilityId = Ability.Id, ChargeRestoreAmount = amount,
                Config = new AbilityTriggerConfig { Type = TriggerType.Signal, SignalId = "recharge" }
            };
            var transaction = new AtomicTransaction();
            trigger.StageBindTarget(transaction, Ability.RuntimeInstanceId);
            if (Ability.Binding.Source is { } source)
            {
                trigger.Binding.StageBind(transaction, source);
                trigger.Binding.StageAuthority(transaction, Context, Owner.Id);
            }
            transaction.Commit();
            TestHandlerFactory.Register(Owner, trigger);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = Owner, Target = Owner,
                AbilityIds = [trigger.Id], Receiver = Owner.EventReceiver
            }).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
            return trigger;
        }

        public void Signal(string signalId)
        {
            Context.Dispatch(Context.Factory.NewEvent<AbilitySignalEvent>() with
            {
                CommandEventId = Guid.NewGuid(), SignalId = signalId, Source = Owner,
                Target = Owner, Receiver = Owner.EventReceiver
            }).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
        }

        public void AuthorizeAndInstall()
        {
            if (Ability.Binding.SourceInstanceId != null)
            {
                var transaction = new AtomicTransaction();
                Ability.Binding.StageAuthority(transaction, Context, Owner.Id);
                transaction.Commit();
            }
            TestHandlerFactory.Register(Owner, Ability);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = Owner, Target = Owner,
                AbilityIds = [Ability.Id], Receiver = Owner.EventReceiver
            }).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
        }

        public CommandReleaseResult Use()
        {
            Guid activation = Guid.NewGuid();
            Assert.IsTrue(Gate.ValidateAndReserve(Context.ContextTimeSpan, null, activation).CanExecute);
            return Gate.CommitRelease(Owner, activation, Context.ContextTimeSpan);
        }

        public AbilityChargeRestoreResultEvent Restore(int amount)
        {
            var request = Context.Factory.NewEvent<AbilityChargeRestoreRequestEvent>() with
            {
                Owner = Owner, AbilityInstanceId = Ability.RuntimeInstanceId, Amount = amount,
                Receiver = Owner.EventReceiver
            };
            Context.Dispatch(request).ToList();
            Advance(Context.ContextTimeSpan.TotalSeconds);
            return Owner.EventReceiver.History.OfType<AbilityChargeRestoreResultEvent>().Last();
        }

        public void Advance(double seconds) => Context.AdvanceTo(TimeSpan.FromSeconds(seconds));

        public void Dispose() => Context.Dispose();
    }
}
