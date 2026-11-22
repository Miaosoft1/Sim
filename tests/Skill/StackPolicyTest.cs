using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class StackPolicyTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void ExtendAndStack_RefreshesLeaseAndAddsStack()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("extend-stack", AbilityStackPolicy.ExtendAndStack);

        Install(context, source, target, status, TimeSpan.Zero);
        Install(context, source, target, status, TimeSpan.FromSeconds(1));
        context.RunUntil(TimeSpan.FromSeconds(5));

        StatusAbility? mounted = target.EventReceiver.GetHandlers<StatusAbility>().FirstOrDefault(m => m.Id == status.Id);
        Assert.IsNotNull(mounted);
        Assert.AreEqual(2, mounted.CurrentStack);

        context.RunUntil(TimeSpan.FromSeconds(6));
        Assert.IsNull(target.EventReceiver.GetHandlers<StatusAbility>().FirstOrDefault(m => m.Id == status.Id));
    }

    [TestMethod]
    public void ExtendOnly_RefreshesLeaseWithoutAddingStack()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("extend-only", AbilityStackPolicy.ExtendOnly);

        Install(context, source, target, status, TimeSpan.Zero);
        Install(context, source, target, status, TimeSpan.FromSeconds(1));
        context.RunUntil(TimeSpan.FromSeconds(5));

        StatusAbility? mounted = target.EventReceiver.GetHandlers<StatusAbility>().FirstOrDefault(m => m.Id == status.Id);
        Assert.IsNotNull(mounted);
        Assert.AreEqual(1, mounted.CurrentStack);

        context.RunUntil(TimeSpan.FromSeconds(6));
        Assert.IsNull(target.EventReceiver.GetHandlers<StatusAbility>().FirstOrDefault(m => m.Id == status.Id));
    }

    [TestMethod]
    public void StackOnly_AddsStackWithoutRefreshingLease()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("stack-only", AbilityStackPolicy.StackOnly);

        Install(context, source, target, status, TimeSpan.Zero);
        Install(context, source, target, status, TimeSpan.FromSeconds(1));
        context.RunUntil(TimeSpan.FromSeconds(1));

        StatusAbility? mounted = target.EventReceiver.GetHandlers<StatusAbility>().FirstOrDefault(m => m.Id == status.Id);
        Assert.IsNotNull(mounted);
        Assert.AreEqual(2, mounted.CurrentStack);

        context.RunUntil(TimeSpan.FromSeconds(5));
        Assert.IsNull(target.EventReceiver.GetHandlers<StatusAbility>().FirstOrDefault(m => m.Id == status.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SameTimeTimedAndPermanentInstalls_LastApplicationControlsExpiry(bool permanentLast)
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("same-time", AbilityStackPolicy.ExtendOnly);
        DispatchInstall(context, source, target, status,
            permanentLast ? TimeSpan.FromSeconds(3) : null, TimeSpan.Zero);
        DispatchInstall(context, source, target, status,
            permanentLast ? null : TimeSpan.FromSeconds(3), TimeSpan.Zero);

        context.AdvanceTo(TimeSpan.FromSeconds(4));

        Assert.AreEqual(permanentLast,
            target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));
        TestContext.WriteLine($"permanent-last={permanentLast}, active={permanentLast}");
    }

    [TestMethod]
    public void RefreshingOneOfTwoLeasesWithSharedCancellation_DoesNotCancelTheOther()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility first = CreateStatus("first", AbilityStackPolicy.ExtendOnly);
        StatusAbility second = CreateStatus("second", AbilityStackPolicy.ExtendOnly);
        using CancellationTokenSource shared = new();
        using CancellationTokenSource replacement = new();
        DispatchInstall(context, source, target, first, TimeSpan.FromSeconds(3), TimeSpan.Zero, shared);
        DispatchInstall(context, source, target, second, TimeSpan.FromSeconds(4), TimeSpan.Zero, shared);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        DispatchInstall(context, source, target, first, TimeSpan.FromSeconds(5), TimeSpan.Zero, replacement);
        context.AdvanceTo(TimeSpan.FromSeconds(1));

        Assert.IsFalse(shared.IsCancellationRequested);
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        Assert.IsTrue(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == first.Id));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == second.Id));
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == first.Id));
        TestContext.WriteLine("shared cancellation source remains active; independent leases expired at 4s and 5s");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RefreshAtExactExpiry_OnlyTheNewStunLeaseControlsTheGate(bool reuseToken)
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("stun", AbilityStackPolicy.ExtendOnly);
        status.Labels |= AbilityLabel.Stunned;
        using CancellationTokenSource original = new();
        using CancellationTokenSource replacement = new();
        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(3), TimeSpan.Zero, original);
        context.AdvanceTo(TimeSpan.Zero);
        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3),
            reuseToken ? original : replacement);

        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.IsTrue(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));
        Assert.AreEqual(TimeSpan.FromSeconds(5),
            target.ActionGate!.GetCastForbiddenUntil(context.ContextTimeSpan, target));
        Assert.IsFalse(original.IsCancellationRequested);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));
        Assert.AreEqual(context.ContextTimeSpan,
            target.ActionGate.GetCastForbiddenUntil(context.ContextTimeSpan, target));
        TestContext.WriteLine($"shared-token={reuseToken}, refreshed stun expired at 5s");
    }

    [TestMethod]
    public void StackOnlyInMixedLease_KeepsOriginalExpiryWhileOtherAbilityRefreshes()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility stackOnly = CreateStatus("stack-only", AbilityStackPolicy.StackOnly);
        StatusAbility refreshable = CreateStatus("refreshable", AbilityStackPolicy.ExtendOnly);
        DispatchInstall(context, source, target, new[] { stackOnly, refreshable },
            TimeSpan.FromSeconds(3), TimeSpan.Zero);
        DispatchInstall(context, source, target, new[] { stackOnly, refreshable },
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, target.EventReceiver.GetHandlers<StatusAbility>()
            .Single(ability => ability.Id == stackOnly.Id).CurrentStack);

        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == stackOnly.Id));
        Assert.IsTrue(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == refreshable.Id));
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == refreshable.Id));
        TestContext.WriteLine("stack-only expired at 3s; refreshable status expired at 5s");
    }

    [TestMethod]
    public void CancelledReplacement_DoesNotInvalidateTheExistingLease()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("cancelled-replacement", AbilityStackPolicy.ExtendOnly);
        using CancellationTokenSource replacement = new();
        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(3), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        context.EventProcessed += ev =>
        {
            if (ev is AbilityInstallEvent install && install.ExpireAt == TimeSpan.FromSeconds(5))
            {
                replacement.Cancel();
            }
        };
        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), replacement);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.IsTrue(replacement.IsCancellationRequested);
        Assert.IsTrue(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));
        TestContext.WriteLine("cancelled replacement left the original 3s expiry intact");
    }

    [TestMethod]
    public void ManualUninstall_PreventsAnOldLeaseRemovingPermanentReinstall()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        StatusAbility status = CreateStatus("manual-reinstall", AbilityStackPolicy.ExtendOnly);
        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(3), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            Source = source,
            Target = target,
            AbilityIds = new[] { status.Id },
            Receiver = target.EventReceiver,
            TimeSpan = TimeSpan.Zero,
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));

        DispatchInstall(context, source, target, status, null, TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        Assert.IsTrue(target.EventReceiver.GetHandlers<StatusAbility>().Any(ability => ability.Id == status.Id));
        Assert.HasCount(1, context.History.OfType<AbilityUninstalledEvent>()
            .Where(ev => ev.AbilityIds.Contains(status.Id)));
        TestContext.WriteLine("permanent reinstall survived the original 3s lease");
    }

    [TestMethod]
    public void VisibilityModifiersFollowStacksLevelsUninstallAndExpiry()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        target.MapObjects.VisibilityStats.SetBase(20, 3);
        StatusAbility status = CreateStatus("visibility", AbilityStackPolicy.ExtendAndStack);
        status.Modifiers =
        [
            Modifier(PropertyModifierTargets.VisionRange, "target.level * 2.5"),
            Modifier(PropertyModifierTargets.Stealth, "-5")
        ];

        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(22.5, target.MapObjects.VisibilityStats.VisionRange, 1e-8);
        Assert.AreEqual(0, target.MapObjects.VisibilityStats.Stealth, 1e-8);

        var renewed = CreateStatus(status.Id, AbilityStackPolicy.ExtendAndStack);
        renewed.Modifiers = status.Modifiers;
        DispatchInstall(context, source, target, renewed, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(25, target.MapObjects.VisibilityStats.VisionRange, 1e-8);
        target.SetLevel(2);
        Assert.AreEqual(30, target.MapObjects.VisibilityStats.VisionRange, 1e-8);

        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = target,
            AbilityIds = [status.Id], Receiver = target.EventReceiver, TimeSpan = TimeSpan.Zero
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(20, target.MapObjects.VisibilityStats.VisionRange, 1e-8);
        Assert.AreEqual(3, target.MapObjects.VisibilityStats.Stealth, 1e-8);

        DispatchInstall(context, source, target, renewed, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(25, target.MapObjects.VisibilityStats.VisionRange, 1e-8);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.AreEqual(20, target.MapObjects.VisibilityStats.VisionRange, 1e-8);
        Assert.AreEqual(3, target.MapObjects.VisibilityStats.Stealth, 1e-8);
        TestContext.WriteLine("Visibility modifiers followed install, stacks, level, uninstall, and lease expiry.");
    }

    [TestMethod]
    public void FailedProjectionDoesNotPartiallyInstallOrBindAbilities()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        StatusAbility good = CreateStatus("good", AbilityStackPolicy.ExtendOnly);
        good.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10")];
        StatusAbility bad = CreateStatus("bad", AbilityStackPolicy.ExtendOnly);
        bad.LifePointMaxMultiplier = decimal.MaxValue;

        DispatchInstall(context, source, target, [good, bad], TimeSpan.FromSeconds(5), TimeSpan.Zero);
        Assert.ThrowsExactly<OverflowException>(() => context.AdvanceTo(TimeSpan.Zero));

        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any());
        Assert.IsNull(good.Binding.BoundContext);
        Assert.IsNull(bad.Binding.BoundContext);
        Assert.IsNull(good.ApplicationSource);
        Assert.IsFalse(context.History.OfType<AbilityInstalledEvent>()
            .Any(ev => ev.AbilityIds.Contains(good.Id) || ev.AbilityIds.Contains(bad.Id)));
        TestContext.WriteLine("Projection failure left all new handlers, bindings, sources and properties unchanged.");
    }

    [TestMethod]
    public void FailedStackProjectionPreservesTheExistingStackAndLease()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        StatusAbility status = CreateStatus("stack", AbilityStackPolicy.ExtendAndStack);
        status.LifePointMaxMultiplier = 10000m;
        DispatchInstall(context, source, target, status, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>()
            .Single(ev => ev.AbilityIds.Contains(status.Id));
        Assert.AreEqual(1000000, target.Property.LifePointMax);

        StatusAbility replacement = CreateStatus(status.Id, AbilityStackPolicy.ExtendAndStack);
        DispatchInstall(context, source, target, replacement, TimeSpan.FromSeconds(10), TimeSpan.Zero);
        Assert.ThrowsExactly<OverflowException>(() => context.AdvanceTo(TimeSpan.Zero));

        Assert.AreSame(status, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(1, status.CurrentStack);
        Assert.AreSame(source, status.ApplicationSource);
        Assert.AreEqual(1000000, target.Property.LifePointMax);
        Assert.IsFalse(lease.LeaseCancellation.IsCancellationRequested);
        Assert.IsNull(replacement.Binding.BoundContext);
        TestContext.WriteLine("A failed second stack preserved the mounted instance, projection and original expiry scope.");
    }

    [TestMethod]
    public void FailedUninstallProjectionPreservesHandlersAndLease()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        StatusAbility suppress = CreateStatus("suppress", AbilityStackPolicy.ExtendOnly);
        suppress.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "-100")];
        StatusAbility explosive = CreateStatus("explosive", AbilityStackPolicy.ExtendOnly);
        explosive.LifePointMaxMultiplier = decimal.MaxValue;
        DispatchInstall(context, source, target, [suppress, explosive], TimeSpan.FromSeconds(5), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        Assert.AreEqual(1, target.Property.LifePointMax);

        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = target,
            AbilityIds = [suppress.Id], Receiver = target.EventReceiver, TimeSpan = TimeSpan.Zero
        }).ToList();
        Assert.ThrowsExactly<OverflowException>(() => context.AdvanceTo(TimeSpan.Zero));

        CollectionAssert.AreEquivalent(new[] { suppress, explosive },
            target.EventReceiver.GetHandlers<StatusAbility>().ToArray());
        Assert.AreEqual(1, target.Property.LifePointMax);
        Assert.IsFalse(lease.LeaseCancellation.IsCancellationRequested);
        Assert.IsFalse(context.History.OfType<AbilityUninstalledEvent>()
            .Any(ev => ev.AbilityIds.Contains(suppress.Id)));
        TestContext.WriteLine("Failed uninstall kept both mounted statuses, their projection and the original lease.");
    }

    [TestMethod]
    public void StrictPropertyFormulasDistinguishAbilityAndCharacterLevels()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        StatusAbility status = CreateStatus("levels", AbilityStackPolicy.ExtendOnly);
        status.Level = 3;
        status.Modifiers = [
            Modifier(PropertyModifierTargets.LifePointMax, "ability.level * 10 + target.level")
        ];
        DispatchInstall(context, source, target, status, null, TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(131, target.Property.LifePointMax);

        target.SetLevel(2);
        Assert.AreEqual(132, target.Property.LifePointMax);
        Assert.AreEqual(3, status.Level);
        TestContext.WriteLine("An ability at level 3 stayed level 3 while target level 1→2 changed the explicitly configured modifier.");
    }

    [TestMethod]
    public void PropertyFormulaFallbackIsExplicitReportedAndMarked()
    {
        (Context strictContext, Unit strictSource, Unit strictTarget) = CreateUnits();
        using var strictCleanup = strictContext;
        strictTarget.Property.Energy = 0;
        StatusAbility strict = CreateStatus("strict", AbilityStackPolicy.ExtendOnly);
        strict.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10 / target.energy", "7")];
        DispatchInstall(strictContext, strictSource, strictTarget, strict, null, TimeSpan.Zero);
        Assert.ThrowsExactly<ExpressionEvaluationException>(() => strictContext.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(100, strictTarget.Property.LifePointMax);
        Assert.IsNull(strict.Binding.BoundContext);
        Assert.IsFalse(strictTarget.EventReceiver.GetHandlers<StatusAbility>().Any());

        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        target.Property.Energy = 0;
        var diagnostics = new List<FormulaDiagnostic>();
        StatusAbility production = CreateStatus("production", AbilityStackPolicy.ExtendOnly);
        production.FormulaPolicy = new FormulaEvaluationPolicy(FormulaEvaluationMode.Production, diagnostics.Add);
        production.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10 / target.energy", "7")];
        DispatchInstall(context, source, target, production, null, TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(107, target.Property.LifePointMax);
        Assert.IsTrue(target.Abilities.UsedFormulaFallback);
        Assert.HasCount(1, diagnostics);
        StringAssert.Contains(diagnostics[0].Error.Message, "Division by zero");

        target.Property.Energy = 10;
        target.Abilities.ReApply();
        Assert.AreEqual(101, target.Property.LifePointMax);
        Assert.IsFalse(target.Abilities.UsedFormulaFallback);
        Assert.HasCount(1, diagnostics);
        TestContext.WriteLine("Strict evaluation rejected division by zero; production reported and marked the explicit fallback until a successful recalculation.");
    }

    [TestMethod]
    public void FailedRealTimeTimerPreparationLeavesExistingStateAndCleansPreparedTimers()
    {
        var clock = new ManualTimeProvider { FailTimerCreationAt = 2 };
        (Context context, Unit source, Unit target) = CreateUnits(clock);
        using var cleanup = context;
        StatusAbility existing = CreateStatus("existing", AbilityStackPolicy.ExtendAndStack);
        DispatchInstall(context, source, target, existing, TimeSpan.FromSeconds(3), TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        AbilityLeaseActivatedEvent previous = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        StatusAbility first = CreateStatus("first-real", AbilityStackPolicy.Independent);
        StatusAbility second = CreateStatus("second-real", AbilityStackPolicy.Independent);
        first.TimeBasis = second.TimeBasis = AbilityTimeBasis.RealTime;
        first.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "5")];
        second.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "5")];

        DispatchInstall(context, source, target, [existing, first, second], null, TimeSpan.Zero);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "Timer creation failed");

        Assert.AreEqual(2, clock.TimerCreations);
        Assert.AreEqual(1, clock.DisposedTimers);
        Assert.AreSame(existing, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(1, existing.CurrentStack);
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.IsFalse(previous.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual("first-real", first.Id);
        Assert.IsNull(first.ApplicationId);
        Assert.IsNull(first.PayloadId);
        Assert.IsNull(first.Binding.BoundContext);
        Assert.IsNull(second.Binding.BoundContext);
        TestContext.WriteLine("A failed second real-time timer cleaned the first and preserved the original status, lease, IDs and projection.");
    }

    [TestMethod]
    public void PreparedRealTimeLeaseExpiresAndRestoresProperties()
    {
        var clock = new ManualTimeProvider();
        (Context context, Unit source, Unit target) = CreateUnits(clock);
        using var cleanup = context;
        StatusAbility status = CreateStatus("real", AbilityStackPolicy.Independent);
        status.TimeBasis = AbilityTimeBasis.RealTime;
        status.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10")];
        DispatchInstall(context, source, target, status, null, TimeSpan.Zero);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.IsNotNull(status.ApplicationId);
        StringAssert.StartsWith(status.Id, "real#");
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();

        clock.Advance(TimeSpan.FromSeconds(4));
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(110, target.Property.LifePointMax);
        clock.Advance(TimeSpan.FromSeconds(1));
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.IsFalse(target.EventReceiver.GetHandlers<StatusAbility>().Any());
        Assert.IsTrue(lease.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual(1, clock.DisposedTimers);
        TestContext.WriteLine("The real-time lease remained active at 4s and expired, disposed its timer and restored properties at 5s.");
    }

    [TestMethod]
    public void CancelingFollowingRealTimeLeaseRemovesTheStatusBeforeExpiry()
    {
        var clock = new ManualTimeProvider();
        (Context context, Unit source, Unit target) = CreateUnits(clock);
        using var cleanup = context;
        using var cancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        StatusAbility status = CreateStatus("real-cancel", AbilityStackPolicy.Independent);
        status.TimeBasis = AbilityTimeBasis.RealTime;
        status.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10")];
        DispatchInstall(context, source, target, status, null, TimeSpan.Zero, cancellation);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(110, target.Property.LifePointMax);

        Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
        context.AdvanceTo(TimeSpan.Zero);

        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.AreEqual(1, clock.DisposedTimers);
        clock.Advance(TimeSpan.FromSeconds(5));
        context.AdvanceTo(TimeSpan.Zero);
        Assert.HasCount(1, context.History.OfType<AbilityUninstalledEvent>()
            .Where(ev => ev.Target == target));
        TestContext.WriteLine("A following real-time lease was canceled and disposed before its timer expired.");
    }

    [TestMethod]
    public void CancelingOneRealTimeLeaseDoesNotRemoveAnotherApplication()
    {
        var clock = new ManualTimeProvider();
        (Context context, Unit source, Unit target) = CreateUnits(clock);
        using var cleanup = context;
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        StatusAbility first = CreateStatus("real-shared", AbilityStackPolicy.Independent);
        StatusAbility second = CreateStatus("real-shared", AbilityStackPolicy.Independent);
        first.TimeBasis = second.TimeBasis = AbilityTimeBasis.RealTime;
        first.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10")];
        second.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10")];
        DispatchInstall(context, source, target, first, null, TimeSpan.Zero, firstCancellation);
        context.AdvanceTo(TimeSpan.Zero);
        DispatchInstall(context, source, target, second, null, TimeSpan.Zero, secondCancellation);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(120, target.Property.LifePointMax);

        firstCancellation.Cancel();
        context.AdvanceTo(TimeSpan.Zero);

        Assert.AreSame(second, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.IsFalse(secondCancellation.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(5));
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("Canceling one real-time application did not remove the other independent application.");
    }

    [TestMethod]
    public void ProtectedRealTimeStatusRejectsFollowingCancellationAndRemainsUntilExpiry()
    {
        var clock = new ManualTimeProvider();
        (Context context, Unit source, Unit target) = CreateUnits(clock);
        using var cleanup = context;
        using var cancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        StatusAbility status = CreateStatus("protected-real", AbilityStackPolicy.Independent);
        status.TimeBasis = AbilityTimeBasis.RealTime;
        status.ProtectedUntilExpiry = true;
        status.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "10")];
        TestHandlerFactory.Register(target, status);
        void Install(AbilityLeaseCancellationPolicy policy)
        {
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = source, Target = target, AbilityIds = [status.Id],
                LeaseCancellationPolicy = policy, Receiver = target.EventReceiver,
                CancellationTokenSource = cancellation
            }).ToList();
        }

        Install(AbilityLeaseCancellationPolicy.FollowRequest);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, clock.TimerCreations);
        Install(AbilityLeaseCancellationPolicy.ContinueAfterCommit);
        context.AdvanceTo(TimeSpan.Zero);
        cancellation.Cancel();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.HasCount(1, target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(110, target.Property.LifePointMax);
        clock.Advance(TimeSpan.FromSeconds(5));
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("Protected real-time status rejected conflicting cancellation and survived until its real-time expiry.");
    }

    [TestMethod]
    public void AppliedStatusStacksByOriginAndActorAndHigherLevelReplacesItsLease()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendAndStack);

        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(5));
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility first = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreSame(rule, first.ApplicationOrigin!.Rule);
        Assert.AreSame(source, first.ApplicationSource);
        Assert.AreEqual("burn", first.PayloadId);
        Assert.AreEqual(110, target.Property.LifePointMax);

        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreSame(first, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(2, first.CurrentStack);
        Assert.AreEqual(120, target.Property.LifePointMax);

        ApplyStatus(context, source, target, origin, "burn", 2, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        StatusAbility replacement = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreNotSame(first, replacement);
        Assert.AreNotEqual(first.ApplicationId, replacement.ApplicationId);
        Assert.AreEqual(2, replacement.Level);
        Assert.AreEqual(1, replacement.CurrentStack);
        Assert.AreEqual(120, target.Property.LifePointMax);
        int installedBeforeIgnore = context.History.OfType<AbilityInstalledEvent>().Count();

        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreSame(replacement, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(installedBeforeIgnore, context.History.OfType<AbilityInstalledEvent>().Count());
        StatusApplicationIgnoredEvent ignored = context.History.OfType<StatusApplicationIgnoredEvent>().Single();
        Assert.AreEqual(1, ignored.AttemptedLevel);
        Assert.AreEqual(2, ignored.ExistingLevel);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.AreSame(replacement, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        context.AdvanceTo(TimeSpan.FromSeconds(12));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("Same-origin level 1 stacked; level 2 replaced and reset the stack; level 1 was ignored and the old lease could not remove level 2.");
    }

    [TestMethod]
    public void DifferentOriginsAndNullableOriginalActorsHaveIndependentGroups()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        Unit otherActor = CreateUnit(context, "other");
        context.AddEventReceiver(otherActor.EventReceiver);
        var firstRule = new TestRule("rule");
        var secondRule = new TestRule("rule");
        source.EventReceiver.AddHandler(firstRule, secondRule);
        var firstOrigin = StatusApplicationOrigin.FromRule(firstRule, source.EventReceiver);
        var secondOrigin = StatusApplicationOrigin.FromRule(secondRule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);

        ApplyStatus(context, source, target, firstOrigin, "burn", 1, null);
        ApplyStatus(context, otherActor, target, firstOrigin, "burn", 2, null);
        ApplyStatus(context, null, target, firstOrigin, "burn", 3, null);
        ApplyStatus(context, source, target, secondOrigin, "burn", 4, null);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility[] statuses = target.EventReceiver.GetHandlers<StatusAbility>().ToArray();
        Assert.HasCount(4, statuses);
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, statuses.Select(status => status.Level).ToArray());
        Assert.AreEqual(200, target.Property.LifePointMax);
        Assert.IsNull(statuses.Single(status => status.Level == 3).ApplicationSource);

        ConfigureAppliedStatusFactory(target, "independent", AbilityStackPolicy.Independent);
        ApplyStatus(context, source, target, firstOrigin, "independent", 1, null);
        ApplyStatus(context, source, target, firstOrigin, "independent", 4, null);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility[] independent = target.EventReceiver.GetHandlers<StatusAbility>()
            .Where(status => status.PayloadId == "independent").ToArray();
        Assert.HasCount(2, independent);
        Assert.AreNotEqual(independent[0].ApplicationId, independent[1].ApplicationId);
        Assert.AreEqual(250, target.Property.LifePointMax);

        source.EventReceiver.TryRemoveHandler(firstRule);
        Assert.AreSame(firstRule, statuses.Single(status => status.Level == 1).ApplicationOrigin!.Rule);
        ApplyStatus(context, source, target, firstOrigin, "independent", 1, null);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.HasCount(6, target.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("Distinct rule instances and original actors, including null, remained separate; independent applications also retained separate leases and levels.");
    }

    [TestMethod]
    public void RealAbilityOriginIsCapturedByTheTriggeredStatusFlow()
    {
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: new TriggerChainFactory());
        using var cleanup = context;
        context.Logger.Output = null;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.Level = 2;
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        bool removedBeforeApplication = false;
        context.EventQueued += queued =>
        {
            if (queued is AbilityInstallRequestEvent { ApplicationOrigin.Ability: var ability } &&
                ReferenceEquals(ability, listener))
                context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
                {
                    CommandEventId = Guid.NewGuid(), Source = source, Target = source,
                    AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver, Priority = int.MinValue
                }).ToList();
        };
        context.EventProcessed += processed =>
        {
            if (processed is AbilityInstallRequestEvent { ApplicationOrigin.Ability: var ability } &&
                ReferenceEquals(ability, listener))
                removedBeforeApplication = !source.EventReceiver.GetHandlers<StatusAbility>()
                    .Any(item => ReferenceEquals(item, listener));
        };

        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility applied = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreSame(listener, applied.ApplicationOrigin!.Ability);
        Assert.AreEqual(listener.RuntimeInstanceId, applied.ApplicationOrigin.AbilityRuntimeInstanceId);
        Assert.AreEqual(2, applied.ApplicationOrigin.AbilitySnapshot!.Level);
        listener.Level = 3;
        Assert.AreEqual(2, applied.ApplicationOrigin.AbilitySnapshot.Level);
        Assert.AreSame(source, applied.ApplicationSource);
        Assert.AreEqual(2, applied.Level);
        Assert.AreEqual(120, target.Property.LifePointMax);
        Assert.IsTrue(removedBeforeApplication);
        TestContext.WriteLine("A released status retained the real ability and actor after its listener was removed before installation.");
    }

    [TestMethod]
    public void TriggeredStatusUsesItsOwnGateAndStartsItsLeaseAtRelease()
    {
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: new TriggerChainFactory());
        using var cleanup = context;
        using var releasedCancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.Level = 1;
        listener.Windup = TimeSpan.FromSeconds(2);
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1),
            ResourceCost = 5, OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        Guid firstCommand = Guid.NewGuid();
        context.EventQueued += ev =>
        {
            if (ev is AbilityActivationResultEvent { Status: CommandExecutionStatus.Success } result &&
                result.CommandEventId == firstCommand)
                releasedCancellation.Cancel();
        };

        for (int i = 0; i < 2; i++)
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = i == 0 ? firstCommand : Guid.NewGuid(), AbilityId = "projectile", Source = source,
                Target = target, Receiver = source.EventReceiver,
                CancellationTokenSource = i == 0 ? releasedCancellation : null
            })).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);

        context.AdvanceTo(TimeSpan.FromSeconds(2));
        StatusAbility applied = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.IsTrue(releasedCancellation.IsCancellationRequested);
        Assert.AreSame(listener, applied.ApplicationOrigin!.Ability);
        Assert.IsNull(applied.GateManager);
        Assert.AreEqual(0, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(15m, source.Property.Energy);
        AbilityActivationResultEvent[] results = source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.AbilityId == listener.Id).ToArray();
        Assert.HasCount(1, results.Where(result => result.Status == CommandExecutionStatus.Success));
        Assert.HasCount(1, results.Where(result => result.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        context.Dispatch(context.Factory.NewEvent<AbilityChargeRestoreRequestEvent>() with
        {
            Owner = source, AbilityInstanceId = listener.RuntimeInstanceId, Amount = 1,
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(1, source.EventReceiver.History.OfType<AbilityChargeRestoreResultEvent>().Single().Restored);
        context.AdvanceTo(TimeSpan.FromSeconds(6));
        Assert.HasCount(1, target.EventReceiver.GetHandlers<StatusAbility>());
        context.AdvanceTo(TimeSpan.FromSeconds(7));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("Two status responses competed for one charge; the release survived later cancellation, accepted recharge, and started its lease at second 2.");
    }

    [TestMethod]
    public void AcceptedStatusCastSurvivesHandlerUnregistrationWithoutAcceptingNewTriggers()
    {
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: new TriggerChainFactory());
        using var cleanup = context;
        context.Logger.Output = null;
        source.Abilities.BaseProperty.EnergyRecoveryPerSecond = 0;
        source.Property.EnergyRecoveryPerSecond = 0;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.Windup = TimeSpan.FromSeconds(2);
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1, TimeSpan.FromSeconds(3), AbilityChargeRecoveryMode.Serial),
            ResourceCost = 5, OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        var grant = new TestAbilitySource(Guid.NewGuid());
        var binding = new AtomicTransaction();
        listener.Binding.StageBind(binding, grant);
        listener.Binding.StageAuthority(binding, context, source.Id);
        binding.Commit();
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);

        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.IsTrue(listener.GateManager.HasPendingReservation);
        context.Dispatch(context.Factory.NewEvent<AtomicAbilityChangeEvent>() with
        {
            Target = source, OperationId = Guid.NewGuid(), Transaction = new AtomicTransaction(),
            Changes = [new AtomicUnitAbilityChange(source, [listener, trigger], [])],
            Completion = context.Factory.NewEvent<Event>() with { Receiver = source.EventReceiver },
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.IsEmpty(source.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(source.EventReceiver.GetHandlers<TriggerHandler>().Where(item => item.Id == trigger.Id));
        decimal energyBeforeRelease = source.Property.Energy;
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));

        StatusAbility applied = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreSame(listener, applied.ApplicationOrigin!.Ability);
        Assert.AreEqual(grant.InstanceId, applied.ApplicationOrigin.AbilityGrantSourceInstanceId);
        Assert.AreEqual(energyBeforeRelease - 5, source.Property.Energy);
        Assert.AreEqual(0, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.AbilityId == listener.Id && result.Status == CommandExecutionStatus.Success));
        AbilityInstallRequestEvent request = target.EventReceiver.History.OfType<AbilityInstallRequestEvent>()
            .Single(item => ReferenceEquals(item.ApplicationOrigin, applied.ApplicationOrigin));
        AbilityInstallEvent install = target.EventReceiver.History.OfType<AbilityInstallEvent>()
            .Single(item => ReferenceEquals(item.ApplicationOrigin, applied.ApplicationOrigin));
        context.Dispatch(request).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(2)));
        context.Dispatch(request with { }).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(2)));
        context.Dispatch(install with { }).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(2)));
        ApplyStatus(context, source, target, StatusApplicationOrigin.FromAbility(listener), "burn", 1, null);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(2)));
        Assert.HasCount(1, target.EventReceiver.GetHandlers<StatusAbility>());
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        Assert.AreEqual(0, listener.GateManager.GetChargeSnapshot()!.Available);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("The accepted cast survived unregistration, its detached charge recovered at second 5, and new triggers and forged/replayed origins were rejected.");
    }

    [TestMethod]
    public void RejectedOrCanceledStatusDoesNotConsumeGate()
    {
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: new TriggerChainFactory());
        using var cleanup = context;
        using var cancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        var registry = new UnitSpatialRegistry();
        registry.Register(source);
        registry.Register(target);
        source.Registry = registry;
        target.Registry = registry;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.Selector = new TrackingSelector { Filter = TargetFilter.Enemy };
        listener.Windup = TimeSpan.FromSeconds(2);
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1),
            ResourceCost = 5, OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);

        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.SelectorRejected));
        target.TeamId = source.TeamId + 1;
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver, CancellationTokenSource = cancellation
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsTrue(listener.GateManager.HasPendingReservation);
        cancellation.Cancel();
        context.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.IsFalse(listener.GateManager.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());

        var binding = new AtomicTransaction();
        listener.Binding.StageBind(binding, new TestAbilitySource(Guid.NewGuid()));
        listener.Binding.StageAuthority(binding, context, source.Id);
        binding.Commit();
        Guid transferredCommand = Guid.NewGuid();
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = transferredCommand, AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.IsTrue(listener.GateManager.HasPendingReservation);
        var transfer = new AtomicTransaction();
        listener.Binding.StageAuthority(transfer, context, target.Id);
        transfer.Commit();
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        Assert.IsFalse(listener.GateManager.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.CommandEventId == transferredCommand &&
                result.FailureReason == CommandExecutionFailureReason.AbilityNotAvailable));
        TestContext.WriteLine("Target rejection, canceled windup and changed ownership consumed neither status charges nor energy.");
    }

    [TestMethod]
    public void TriggeredStatusRejectsTargetKilledDuringWindupWithoutConsumingGate()
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.Windup = TimeSpan.FromSeconds(2);
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1),
            ResourceCost = 5, Cooldown = TimeSpan.FromSeconds(3), OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        target.Property.LifePoint = 5;

        Guid command = Guid.NewGuid();
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = command, AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.Dispatch(context.Factory.NewEvent<PeriodicDamageRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = target, Damage = damage,
            TickCount = 1, TickInterval = TimeSpan.FromSeconds(1), FirstTickDelay = TimeSpan.FromSeconds(1),
            Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.IsTrue(listener.GateManager.HasPendingReservation);
        Assert.IsFalse(target.IsAlive);
        CollectionAssert.AreEqual(new[] { 5 }, LifeReductions(context, target));

        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, listener.GateManager.NextReadyTime);
        Assert.IsFalse(listener.GateManager.HasPendingReservation);
        AbilityActivationResultEvent result = source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Single(item => item.CommandEventId == command);
        Assert.AreEqual(CommandExecutionStatus.Failure, result.Status);
        Assert.AreEqual(CommandExecutionFailureReason.TargetNotExist, result.FailureReason);
        TestContext.WriteLine("A five-point periodic hit killed the target during windup; the status failed without consuming charge, energy or cooldown.");
    }

    [TestMethod]
    public void TriggeredStatusFiltersDeadTargetButAppliesToRemainingTarget()
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit survivor = CreateUnit(context, "survivor");
        context.AddEventReceiver(survivor.EventReceiver);
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        source.Registry = new UnitSpatialRegistry();
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.Selector = new FixedTargetsSelector(target, survivor);
        listener.Windup = TimeSpan.FromSeconds(2);
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1), OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ConfigureAppliedStatusFactory(survivor, "burn", AbilityStackPolicy.ExtendOnly);
        target.Property.LifePoint = 5;

        Guid command = Guid.NewGuid();
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = command, AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.Dispatch(context.Factory.NewEvent<PeriodicDamageRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = target, Damage = damage,
            TickCount = 1, TickInterval = TimeSpan.FromSeconds(1), FirstTickDelay = TimeSpan.FromSeconds(1),
            Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.IsFalse(target.IsAlive);
        CollectionAssert.AreEqual(new[] { 5 }, LifeReductions(context, target));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, survivor.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, listener.GateManager.GetChargeSnapshot()!.Available);
        AbilityActivationResultEvent result = source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Single(item => item.CommandEventId == command);
        Assert.AreEqual(CommandExecutionStatus.Success, result.Status);
        TestContext.WriteLine("The five-point hit removed one target during windup; the status released once on the surviving target.");
    }

    [TestMethod]
    public void StatusListenerCannotBeAddressedAsAnActiveCommand()
    {
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: new TriggerChainFactory());
        using var cleanup = context;
        context.Logger.Output = null;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1), OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id, TriggerType = TriggerType.Command
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        var command = context.Factory.NewEvent<CommandEvent>() with
        {
            AbilityId = listener.Id, Source = source, Target = target, Receiver = source.EventReceiver
        };
        context.Dispatch(command).ToList();
        context.AdvanceTo(TimeSpan.Zero);

        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        CommandExecutionEvent result = source.EventReceiver.History.OfType<CommandExecutionEvent>()
            .Single(item => ReferenceEquals(item.Command, command));
        Assert.AreEqual(CommandExecutionStatus.Failure, result.Status);
        Assert.AreEqual(CommandExecutionFailureReason.AbilityNotAvailable, result.FailureReason);
        TestContext.WriteLine("A status listener was rejected as a direct command without applying a status or consuming its charge.");
    }

    [TestMethod]
    public void TriggeredStatusHonorsConfiguredCooldownWithoutCharges()
    {
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: new TriggerChainFactory());
        using var cleanup = context;
        context.Logger.Output = null;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendAndStack);
        listener.IsTriggerListener = true;
        listener.PayloadId = "burn";
        listener.GateManager = new CommandGateManager { Cooldown = TimeSpan.FromSeconds(3), OccupiesWindup = false };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendAndStack);

        void Hit()
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
                Target = target, Receiver = source.EventReceiver
            })).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }
        Hit();
        Hit();
        StatusAbility status = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreEqual(1, status.CurrentStack);
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.CoolingDown));
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Hit();
        Assert.AreSame(status, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(2, status.CurrentStack);
        Assert.IsNull(listener.GateManager.GetChargeSnapshot());
        TestContext.WriteLine("An uncharged status respected its three-second cooldown and stacked again only after it expired.");
    }

    [TestMethod]
    public void FailedHigherLevelApplicationKeepsTheOriginalStatusAndLease()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendAndStack);
        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(5));
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        int installedBefore = context.History.OfType<AbilityInstalledEvent>().Count();
        AbilityInstallHandler installer = target.EventReceiver.Get<AbilityInstallHandler>()!;
        installer.AbilityFactory = (id, level) =>
        [
            new StatusAbility
            {
                Id = id, PayloadId = id, IsTriggerListener = true, Level = level ?? 1,
                StackPolicy = AbilityStackPolicy.ExtendAndStack, LifePointMaxMultiplier = decimal.MaxValue,
                Labels = AbilityLabel.Status
            }
        ];

        ApplyStatus(context, source, target, origin, "burn", 2, TimeSpan.FromSeconds(10));
        Assert.ThrowsExactly<OverflowException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(1, original.CurrentStack);
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.IsFalse(lease.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual(installedBefore, context.History.OfType<AbilityInstalledEvent>().Count());
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("A failed level-2 projection left the original level-1 status and its original expiry intact.");
    }

    [TestMethod]
    public void FailedStatusNotificationReportsThatReplacementAlreadyCommitted()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(5));
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        context.EventQueued += ev =>
        {
            if (ev is AbilityInstalledEvent)
                throw new InvalidOperationException("status observer failed");
        };
        ApplyStatus(context, source, target, origin, "burn", 2, TimeSpan.FromSeconds(10));

        var failure = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(failure.InnerException!.ToString(), "status observer failed");
        StatusAbility replacement = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreNotSame(original, replacement);
        Assert.AreEqual(2, replacement.Level);
        Assert.AreEqual(120, target.Property.LifePointMax);
        Assert.IsTrue(lease.LeaseCancellation.IsCancellationRequested);
        TestContext.WriteLine("An observer failure after level-2 replacement reported a committed operation; the old lease was already invalidated.");
    }

    [TestMethod]
    public void FullPeriodicQuotaRejectsTheEntireStatusApplication()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add, maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var firstRule = new TestRule("first");
        var secondRule = new TestRule("second");
        source.EventReceiver.AddHandler(firstRule, secondRule);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendAndStack);

        ApplyStatus(context, source, target, StatusApplicationOrigin.FromRule(firstRule, source.EventReceiver),
            "burn", 1, TimeSpan.FromSeconds(10), effects: [Periodic(damage, 4)]);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        AbilityLeaseActivatedEvent originalLease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        int installedBefore = context.History.OfType<AbilityInstalledEvent>().Count();

        Guid rejectedId = ApplyStatus(context, source, target,
            StatusApplicationOrigin.FromRule(secondRule, source.EventReceiver),
            "burn", 2, TimeSpan.FromSeconds(8), effects: [Periodic(damage, 1)]);
        context.AdvanceTo(TimeSpan.Zero);

        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.IsFalse(originalLease.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(installedBefore, context.History.OfType<AbilityInstalledEvent>().Count());
        StatusApplicationRejectedEvent rejected = context.History.OfType<StatusApplicationRejectedEvent>().Single();
        Assert.AreEqual(rejectedId, rejected.CommandEventId);
        Assert.AreEqual(PeriodicEffectRejectionReason.Capacity, rejected.Reason);
        Assert.AreEqual("burn", rejected.StatusId);
        Assert.AreEqual(damage.Id, rejected.PeriodicEffectId);
        Assert.HasCount(1, diagnostics);

        Guid rejectedReplacement = ApplyStatus(context, source, target,
            StatusApplicationOrigin.FromRule(firstRule, source.EventReceiver),
            "burn", 2, TimeSpan.FromSeconds(8), effects: [Periodic(damage, 1), Periodic(damage, 2)]);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.IsFalse(originalLease.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(installedBefore, context.History.OfType<AbilityInstalledEvent>().Count());
        Assert.AreEqual(rejectedReplacement,
            context.History.OfType<StatusApplicationRejectedEvent>().Last().CommandEventId);
        Assert.HasCount(2, diagnostics);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(context, target));
        TestContext.WriteLine("A full quota rejected the second status without changing the first status, lease, projection or two scheduled hits.");
    }

    [TestMethod]
    public void GatedStatusQuotaRejectionDoesNotConsumeResourcesOrReportSuccess()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add, maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        target.Property.LifePoint = 40;
        HealProfile healing = HealProfile.From(CreateScheduledHeal());
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "regeneration";
        listener.PeriodicEffects = [PeriodicHeal(healing, 1), PeriodicHeal(healing, 2)];
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1), ResourceCost = 5,
            Cooldown = TimeSpan.FromSeconds(3), OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        source.Property.EnergyRecoveryPerSecond = 0;
        source.Abilities.BaseProperty.EnergyRecoveryPerSecond = 0;
        ConfigureAppliedStatusFactory(target, "regeneration", AbilityStackPolicy.ExtendOnly);
        Guid rejectedCommand = Guid.NewGuid();
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = rejectedCommand, AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);

        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, listener.GateManager.NextReadyTime);
        Assert.IsFalse(listener.GateManager.HasPendingReservation);
        AbilityActivationResultEvent failure = source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Single(result => result.CommandEventId == rejectedCommand);
        Assert.AreEqual(CommandExecutionStatus.Failure, failure.Status);
        Assert.AreEqual(CommandExecutionFailureReason.PeriodicEffectLimit, failure.FailureReason);
        Assert.AreEqual(PeriodicEffectRejectionReason.Capacity,
            context.History.OfType<StatusApplicationRejectedEvent>().Single().Reason);
        Assert.HasCount(1, diagnostics);

        listener.PeriodicEffects = [PeriodicHeal(healing, 1)];
        Guid acceptedCommand = Guid.NewGuid();
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = acceptedCommand, AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(15m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.FromSeconds(3), listener.GateManager.NextReadyTime);
        Assert.AreEqual(50, target.Property.LifePoint);
        Assert.AreSame(listener, target.EventReceiver.GetHandlers<StatusAbility>().Single().ApplicationOrigin!.Ability);
        Assert.AreEqual(CommandExecutionStatus.Success, source.EventReceiver.History
            .OfType<AbilityActivationResultEvent>().Single(result => result.CommandEventId == acceptedCommand).Status);
        TestContext.WriteLine("Quota rejection preserved the gate; a one-effect retry committed and healed twice.");
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, true)]
    public void GatedStatusCommitsAllTargetsOrNone(int capacity, bool accepted)
    {
        var factory = new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: _ => { }, maxActivePeriodicEffects: capacity);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(second.EventReceiver);
        source.Registry = new UnitSpatialRegistry();
        first.Property.LifePoint = 40;
        second.Property.LifePoint = 40;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "regeneration";
        listener.Selector = new FixedTargetsSelector(first, second);
        listener.PeriodicEffects = [PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1)];
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1), ResourceCost = 5,
            Cooldown = TimeSpan.FromSeconds(3), OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        ConfigureAppliedStatusFactory(first, "regeneration", AbilityStackPolicy.ExtendOnly);
        ConfigureAppliedStatusFactory(second, "regeneration", AbilityStackPolicy.ExtendOnly);
        Guid command = Guid.NewGuid();
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = command, AbilityId = "projectile", Source = source,
            Target = first, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);

        Assert.HasCount(accepted ? 1 : 0, first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(accepted ? 1 : 0, second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(accepted ? 2 : 0, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(accepted ? 0 : 1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(accepted ? 15m : 20m, source.Property.Energy);
        Assert.AreEqual(accepted ? TimeSpan.FromSeconds(3) : TimeSpan.Zero, listener.GateManager.NextReadyTime);
        AbilityActivationResultEvent result = source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Single(item => item.CommandEventId == command);
        Assert.AreEqual(accepted ? CommandExecutionStatus.Success : CommandExecutionStatus.Failure, result.Status);
        if (!accepted)
            Assert.AreEqual(CommandExecutionFailureReason.PeriodicEffectLimit, result.FailureReason);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(accepted ? 50 : 40, first.Property.LifePoint);
        Assert.AreEqual(accepted ? 50 : 40, second.Property.LifePoint);
        TestContext.WriteLine($"Capacity {capacity}: accepted={accepted}; both targets and gate agree.");
    }

    [TestMethod]
    public void GatedStatusRepreparesChangedLevelAndRejectsAnOldRound()
    {
        var (context, _, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        AbilityInstallEvent? oldInstall = null;
        bool changed = false;
        context.EventProcessed += ev =>
        {
            if (ev is AbilityInstallEvent { StatusBatch: not null } install && oldInstall == null)
                oldInstall = install;
            if (ev is StatusApplicationBatchReadyEvent && !changed)
            {
                listener.Level = 2;
                changed = true;
            }
        };
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.Zero);

        Assert.IsTrue(changed);
        Assert.IsNotNull(oldInstall);
        StatusAbility firstStatus = first.EventReceiver.GetHandlers<StatusAbility>().Single();
        StatusAbility secondStatus = second.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreEqual(2, firstStatus.Level);
        Assert.AreEqual(2, secondStatus.Level);
        Assert.AreEqual(2, firstStatus.ApplicationOrigin!.AbilitySnapshot!.Level);
        Assert.AreEqual(2, secondStatus.ApplicationOrigin!.AbilitySnapshot!.Level);
        Assert.HasCount(4, context.History.OfType<AbilityInstallEvent>().Where(ev => ev.StatusBatch != null));
        context.Dispatch(oldInstall).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.AreSame(firstStatus, first.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreSame(secondStatus, second.EventReceiver.GetHandlers<StatusAbility>().Single());
        TestContext.WriteLine("A changed release level caused a fresh two-target round; an old installation could not be replayed.");
    }

    [TestMethod]
    public void GatedStatusRepreparesWhenTheSelectedTargetsChange()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        bool changed = false;
        context.EventProcessed += ev =>
        {
            if (ev is StatusApplicationBatchReadyEvent && !changed)
            {
                listener.Selector = new FixedTargetsSelector(second);
                changed = true;
            }
        };
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsTrue(changed);
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(40, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        TestContext.WriteLine("A changed target selection was re-prepared without applying the obsolete target.");
    }

    [TestMethod]
    public void GatedStatusCanceledAfterPreparationDoesNotConsumeGate()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        using var cancellation = new CancellationTokenSource();
        context.EventProcessed += ev =>
        {
            if (ev is StatusApplicationBatchReadyEvent) cancellation.Cancel();
        };
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = first, Receiver = source.EventReceiver, CancellationTokenSource = cancellation
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsFalse(listener.GateManager!.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, listener.GateManager.NextReadyTime);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("Canceling after both targets prepared left the gate and targets unchanged.");
    }

    [TestMethod]
    public void GatedStatusOwnershipChangeAfterPreparationDoesNotConsumeGate()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        var binding = new AtomicTransaction();
        listener.Binding.StageBind(binding, new TestAbilitySource(Guid.NewGuid()));
        listener.Binding.StageAuthority(binding, context, source.Id);
        binding.Commit();
        bool transferred = false;
        context.EventProcessed += ev =>
        {
            if (ev is StatusApplicationBatchReadyEvent && !transferred)
            {
                var change = new AtomicTransaction();
                listener.Binding.StageAuthority(change, context, second.Id);
                change.Commit();
                transferred = true;
            }
        };
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsTrue(transferred);
        Assert.IsFalse(listener.GateManager!.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, listener.GateManager.NextReadyTime);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(item => item.FailureReason == CommandExecutionFailureReason.AbilityNotAvailable));
        TestContext.WriteLine("A transferred source failed its final ownership check without consuming the gate.");
    }

    [TestMethod]
    public void GatedStatusSelfTargetCombinesCostWithPropertyProjection()
    {
        var (context, _, source, first, _, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        listener.Selector = new FixedTargetsSelector(source, first);
        source.Abilities.BaseProperty.EnergyMax = 20;
        source.Property.EnergyMax = 20;
        source.Property.Energy = 12;
        source.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory = (id, level) =>
        [
            new StatusAbility
            {
                Id = id, PayloadId = id, IsTriggerListener = true, Level = level ?? 1,
                StackPolicy = AbilityStackPolicy.ExtendOnly, EnergyMaxMultiplier = 0.5m,
                Labels = AbilityLabel.Status
            }
        ];
        int previousEnergyEvents = context.History.OfType<UnitEnergyChangedEvent>().Count(ev => ev.Unit == source);
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(10, source.Property.EnergyMax);
        Assert.AreEqual(7m, source.Property.Energy);
        Assert.HasCount(1, source.EventReceiver.GetHandlers<StatusAbility>().Where(status => !status.IsTriggerListener));
        Assert.HasCount(1, first.EventReceiver.GetHandlers<StatusAbility>());
        UnitEnergyChangedEvent energy = context.History.OfType<UnitEnergyChangedEvent>()
            .Where(ev => ev.Unit == source).Skip(previousEnergyEvents).Single();
        Assert.AreEqual(7m, energy.Energy);
        Assert.AreEqual(10, energy.EnergyMax);
        TestContext.WriteLine("Self-target energy was charged before the new maximum was applied.");
    }

    [TestMethod]
    public void GatedStatusReplacesBothTargetsAtNetPeriodicCapacity()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        listener.Duration = TimeSpan.FromSeconds(20);
        listener.PeriodicEffects = [PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 10)];
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility firstOriginal = first.EventReceiver.GetHandlers<StatusAbility>().Single();
        StatusAbility secondOriginal = second.EventReceiver.GetHandlers<StatusAbility>().Single();
        AbilityLeaseActivatedEvent[] oldLeases = context.History.OfType<AbilityLeaseActivatedEvent>()
            .Where(item => item.Target == first || item.Target == second).ToArray();
        Assert.HasCount(2, oldLeases);
        context.Dispatch(context.Factory.NewEvent<AbilityChargeRestoreRequestEvent>() with
        {
            Owner = source, AbilityInstanceId = listener.RuntimeInstanceId, Amount = 1,
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        listener.Level = 2;
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        Assert.AreNotSame(firstOriginal, first.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreNotSame(secondOriginal, second.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(2, first.EventReceiver.GetHandlers<StatusAbility>().Single().Level);
        Assert.AreEqual(2, second.EventReceiver.GetHandlers<StatusAbility>().Single().Level);
        Assert.IsTrue(oldLeases.All(lease => lease.LeaseCancellation.IsCancellationRequested));
        context.AdvanceTo(TimeSpan.FromSeconds(11));
        Assert.AreEqual(40, first.Property.LifePoint);
        Assert.AreEqual(40, second.Property.LifePoint);
        context.AdvanceTo(TimeSpan.FromSeconds(14));
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        TestContext.WriteLine("At capacity, two old effects and leases were replaced by two new ones; only the new ticks settled.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GatedStatusRealTimeLeasesCommitTogetherOrCleanUp(bool failSecondTimer)
    {
        var clock = new ManualTimeProvider { FailTimerCreationAt = failSecondTimer ? 2 : 0 };
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch(clock);
        using var cleanup = context;
        listener.TimeBasis = AbilityTimeBasis.RealTime;
        listener.StackPolicy = AbilityStackPolicy.Independent;
        foreach (Unit target in new[] { first, second })
            target.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory = (id, level) =>
            [
                new StatusAbility
                {
                    Id = id, PayloadId = id, IsTriggerListener = true, Level = level ?? 1,
                    StackPolicy = AbilityStackPolicy.Independent, TimeBasis = AbilityTimeBasis.RealTime,
                    Duration = TimeSpan.FromSeconds(5), Labels = AbilityLabel.Status
                }
            ];
        DispatchStatusHit(context, source, first);
        if (failSecondTimer)
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
            StringAssert.Contains(error.Message, "Timer creation failed");
            Assert.AreEqual(1, clock.DisposedTimers);
            Assert.AreEqual(1, listener.GateManager!.GetChargeSnapshot()!.Available);
            Assert.AreEqual(20m, source.Property.Energy);
            Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
            Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
            Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        }
        else
        {
            context.AdvanceTo(TimeSpan.Zero);
            Assert.HasCount(1, first.EventReceiver.GetHandlers<StatusAbility>());
            Assert.HasCount(1, second.EventReceiver.GetHandlers<StatusAbility>());
            clock.Advance(TimeSpan.FromSeconds(5));
            context.AdvanceTo(TimeSpan.Zero);
            Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
            Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
            Assert.AreEqual(2, clock.DisposedTimers);
            Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        }
        TestContext.WriteLine($"Real-time second-timer failure={failSecondTimer}; both targets remained consistent.");
    }

    [TestMethod]
    public void GatedStatusPreparationFailureLeavesAllTargetsAndGateUnchanged()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        second.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory = (id, level) =>
        [
            new StatusAbility
            {
                Id = id, PayloadId = id, IsTriggerListener = true, Level = level ?? 1,
                StackPolicy = AbilityStackPolicy.ExtendOnly, LifePointMaxMultiplier = decimal.MaxValue
            }
        ];
        DispatchStatusHit(context, source, first);
        Assert.ThrowsExactly<OverflowException>(() => context.AdvanceTo(TimeSpan.Zero));

        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(1, listener.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, listener.GateManager.NextReadyTime);
        Assert.IsFalse(listener.GateManager.HasPendingReservation);
        ConfigureAppliedStatusFactory(second, "regeneration", AbilityStackPolicy.ExtendOnly);
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        TestContext.WriteLine("Second-target projection failure rolled back both targets and allowed a successful retry.");
    }

    [TestMethod]
    public void GatedStatusFactoryFailureReleasesItsReservation()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        second.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory = (_, _) =>
            throw new InvalidOperationException("status factory failed");
        DispatchStatusHit(context, source, first);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "status factory failed");

        Assert.IsFalse(listener.GateManager!.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        ConfigureAppliedStatusFactory(second, "regeneration", AbilityStackPolicy.ExtendOnly);
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        TestContext.WriteLine("A target factory failure released the reservation without applying either status.");
    }

    [TestMethod]
    public void GatedStatusDeliveryObserverFailureAbortsTheBatch()
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        bool failed = false;
        Action<IEvent> observer = ev =>
        {
            if (ev is AbilityInstallEvent { StatusBatch: not null } && !failed)
            {
                failed = true;
                throw new InvalidOperationException("status delivery observer failed");
            }
        };
        context.EventQueued += observer;
        DispatchStatusHit(context, source, first);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "status delivery observer failed");
        context.EventQueued -= observer;
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsTrue(failed);
        Assert.IsFalse(listener.GateManager!.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        DispatchStatusHit(context, source, first);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        TestContext.WriteLine("A pre-commit delivery observer failure aborted stale queued work and allowed a fresh retry.");
    }

    [TestMethod]
    [DataRow("request")]
    [DataRow("install")]
    [DataRow("ready")]
    public void GatedStatusProcessingObserverFailureAbortsTheBatch(string phase)
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        Action<IEvent> observer = ev =>
        {
            if (phase == "request" && ev is AbilityInstallRequestEvent { StatusBatch: not null } ||
                phase == "install" && ev is AbilityInstallEvent { StatusBatch: not null } ||
                phase == "ready" && ev is StatusApplicationBatchReadyEvent)
                throw new InvalidOperationException("status processing observer failed");
        };
        context.EventProcessed += observer;
        DispatchStatusHit(context, source, first);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "status processing observer failed");
        context.EventProcessed -= observer;
        context.AdvanceTo(TimeSpan.Zero);
        Assert.IsFalse(listener.GateManager!.HasPendingReservation);
        Assert.AreEqual(1, listener.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine($"A pre-handler processing observer failure at {phase} aborted the batch.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GatedStatusObserverFailureStillSchedulesEveryCommittedLeaseAndTick(bool chargeNotification)
    {
        var (context, factory, source, first, second, listener) = CreateGatedStatusBatch();
        using var cleanup = context;
        bool failed = false;
        Action<IEvent> observer = ev =>
        {
            if (!failed && (chargeNotification && ev is AbilityChargesChangedEvent ||
                !chargeNotification && ev is UnitEnergyChangedEvent))
            {
                failed = true;
                throw new InvalidOperationException("status observer failed");
            }
        };
        context.EventQueued += observer;
        DispatchStatusHit(context, source, first);
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.ToString(), "status observer failed");
        context.EventQueued -= observer;

        Assert.IsTrue(failed);
        Assert.AreEqual(0, listener.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(15m, source.Property.Energy);
        Assert.HasCount(1, first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.HasCount(1, second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.IsEmpty(first.EventReceiver.GetHandlers<StatusAbility>());
        Assert.IsEmpty(second.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine($"Committed observer failure during {(chargeNotification ? "charge" : "energy")} publication was reported; both targets healed twice and expired.");
    }

    [TestMethod]
    public void PeriodicPreparationsForDifferentTargetsShareOneTransaction()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 2);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(second.EventReceiver);
        first.Property.LifePoint = 50;
        second.Property.LifePoint = 50;
        var rule = new TestRule("batch-origin");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        var batch = new PeriodicPreparationProbe(source, [first, second], origin,
            PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1));
        source.EventReceiver.AddHandler(batch);

        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        PeriodicEffectStartedEvent[] starts = context.History.OfType<PeriodicEffectStartedEvent>().ToArray();
        Assert.HasCount(2, starts);
        Assert.AreNotEqual(starts[0].Effect.StatusApplicationId, starts[1].Effect.StatusApplicationId);
        Assert.IsTrue(starts.All(start => ReferenceEquals(origin, start.Effect.ApplicationOrigin)));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(60, first.Property.LifePoint);
        Assert.AreEqual(60, second.Property.LifePoint);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("Two independently prepared targets committed together and each received two heals.");
    }

    [TestMethod]
    public void PeriodicPreparationFailureDoesNotPublishEarlierStagedTargets()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production,
            report: diagnostics.Add, maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(second.EventReceiver);
        first.Property.LifePoint = 50;
        second.Property.LifePoint = 50;
        var rule = new TestRule("batch-origin");
        source.EventReceiver.AddHandler(rule);
        var batch = new PeriodicPreparationProbe(source, [first, second],
            StatusApplicationOrigin.FromRule(rule, source.EventReceiver),
            PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1));
        source.EventReceiver.AddHandler(batch);

        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(PeriodicEffectRejectionReason.Capacity, batch.Rejection);
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(context.History.OfType<PeriodicEffectStartedEvent>());
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        TestContext.WriteLine("Rejecting the second staged target closed the first reservation without publishing either.");
    }

    [TestMethod]
    public void PeriodicPreparationsUseNetCapacityAcrossDifferentTargets()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 2);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        Unit third = CreateUnit(context, "third");
        Unit fourth = CreateUnit(context, "fourth");
        foreach (Unit unit in new[] { second, third, fourth }) context.AddEventReceiver(unit.EventReceiver);
        foreach (Unit unit in new[] { first, second, third, fourth }) unit.Property.LifePoint = 50;
        var rule = new TestRule("batch-origin");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        var effect = PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1);
        var initial = new PeriodicPreparationProbe(source, [first, second], origin, effect);
        source.EventReceiver.AddHandler(initial);
        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle[] old = context.History.OfType<PeriodicEffectStartedEvent>()
            .Select(start => start.Effect).ToArray();
        source.EventReceiver.TryRemoveHandler(initial);

        var replacements = new PeriodicPreparationProbe(source, [third, fourth], origin, effect,
            old.Select(handle => handle.StatusApplicationId!.Value).ToHashSet());
        source.EventReceiver.AddHandler(replacements);
        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        Assert.IsTrue(old.All(handle => handle.Cancellation.IsCancellationRequested));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(50, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        Assert.AreEqual(60, third.Property.LifePoint);
        Assert.AreEqual(60, fourth.Property.LifePoint);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("Both old targets were retired before two new targets consumed the same net quota.");
    }

    [TestMethod]
    public void RejectedPeriodicReplacementsKeepAllOldTargetsActive()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production,
            report: diagnostics.Add, maxActivePeriodicEffects: 2);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        Unit third = CreateUnit(context, "third");
        Unit fourth = CreateUnit(context, "fourth");
        Unit fifth = CreateUnit(context, "fifth");
        foreach (Unit unit in new[] { second, third, fourth, fifth }) context.AddEventReceiver(unit.EventReceiver);
        foreach (Unit unit in new[] { first, second, third, fourth, fifth }) unit.Property.LifePoint = 50;
        var rule = new TestRule("batch-origin");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        var effect = PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1);
        var initial = new PeriodicPreparationProbe(source, [first, second], origin, effect);
        source.EventReceiver.AddHandler(initial);
        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle[] old = context.History.OfType<PeriodicEffectStartedEvent>()
            .Select(start => start.Effect).ToArray();
        source.EventReceiver.TryRemoveHandler(initial);

        var replacements = new PeriodicPreparationProbe(source, [third, fourth, fifth], origin, effect,
            old.Select(handle => handle.StatusApplicationId!.Value).ToHashSet());
        source.EventReceiver.AddHandler(replacements);
        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(PeriodicEffectRejectionReason.Capacity, replacements.Rejection);
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        Assert.IsTrue(old.All(handle => !handle.Cancellation.IsCancellationRequested));
        Assert.HasCount(2, context.History.OfType<PeriodicEffectStartedEvent>());
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(60, first.Property.LifePoint);
        Assert.AreEqual(60, second.Property.LifePoint);
        Assert.AreEqual(50, third.Property.LifePoint);
        Assert.AreEqual(50, fourth.Property.LifePoint);
        Assert.AreEqual(50, fifth.Property.LifePoint);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("A rejected net replacement kept both old effects and their scheduled heals intact.");
    }

    [TestMethod]
    public void PeriodicStatusBatchUsesAllTargetsNetQuotaAndTheirOwnOrigins()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 2);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(second.EventReceiver);
        first.Property.LifePoint = second.Property.LifePoint = 50;
        var firstRule = new TestRule("first-origin");
        var secondRule = new TestRule("second-origin");
        source.EventReceiver.AddHandler(firstRule, secondRule);
        var firstOrigin = StatusApplicationOrigin.FromRule(firstRule, source.EventReceiver);
        var secondOrigin = StatusApplicationOrigin.FromRule(secondRule, source.EventReceiver);
        var effect = PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1);
        var initial = new PeriodicPreparationProbe(source, [first, second], firstOrigin, effect);
        source.EventReceiver.AddHandler(initial);
        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle[] old = context.History.OfType<PeriodicEffectStartedEvent>()
            .Select(start => start.Effect).ToArray();
        source.EventReceiver.TryRemoveHandler(initial);
        Guid firstApplication = Guid.NewGuid();
        var batch = new PeriodicStatusBatchProbe(source,
        [
            new(first, [effect with { Replaces = old[0] }, effect], firstApplication,
                old[0].StatusApplicationId, firstOrigin),
            new(second, [], Guid.NewGuid(), old[1].StatusApplicationId, secondOrigin)
        ]);
        source.EventReceiver.AddHandler(batch);

        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        Assert.IsTrue(old.All(handle => handle.Cancellation.IsCancellationRequested));
        PeriodicEffectHandle[] added = context.History.OfType<PeriodicEffectStartedEvent>()
            .Select(start => start.Effect).Where(handle => !old.Contains(handle)).ToArray();
        Assert.HasCount(2, added);
        Assert.IsTrue(added.All(handle => handle.StatusApplicationId == firstApplication &&
            ReferenceEquals(handle.ApplicationOrigin, firstOrigin)));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(70, first.Property.LifePoint);
        Assert.AreEqual(50, second.Property.LifePoint);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("A two-target status replacement used net quota and retained the first target's origin.");
    }

    [TestMethod]
    public void PeriodicStatusBatchCannotReplaceAnotherTargetsApplication()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 2);
        (Context context, Unit source, Unit first) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(second.EventReceiver);
        var rule = new TestRule("origin");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        var effect = PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1);
        var initial = new PeriodicPreparationProbe(source, [first, second], origin, effect);
        source.EventReceiver.AddHandler(initial);
        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle[] old = context.History.OfType<PeriodicEffectStartedEvent>()
            .Select(start => start.Effect).ToArray();
        source.EventReceiver.TryRemoveHandler(initial);
        source.EventReceiver.AddHandler(new PeriodicStatusBatchProbe(source,
        [
            new(first, [effect], Guid.NewGuid(), old[1].StatusApplicationId, origin)
        ]));

        context.Dispatch(context.Factory.NewEvent<PeriodicPreparationProbeEvent>() with
        {
            Receiver = source.EventReceiver
        }).ToList();
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "another target");
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(context));
        Assert.IsTrue(old.All(handle => !handle.Cancellation.IsCancellationRequested));
        TestContext.WriteLine("A batch could not retire another target's status effects.");
    }

    [TestMethod]
    public void StatusReplacementUsesNetQuotaAndKeepsActorlessDamageIndependentOfTheOldLease()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, null, target, origin, "burn", 1, TimeSpan.FromSeconds(3),
            effects: [Periodic(damage, 2)]);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        PeriodicEffectHandle oldEffect = context.History.OfType<PeriodicEffectStartedEvent>().Single().Effect;

        ApplyStatus(context, null, target, origin, "burn", 2, TimeSpan.FromSeconds(7),
            effects: [Periodic(damage, 4)]);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility replacement = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        PeriodicEffectHandle newEffect = context.History.OfType<PeriodicEffectStartedEvent>().Last().Effect;
        Assert.AreNotSame(original, replacement);
        Assert.AreNotSame(oldEffect, newEffect);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.IsNull(replacement.ApplicationSource);
        Assert.AreSame(rule, replacement.ApplicationOrigin!.Rule);
        source.EventReceiver.TryRemoveHandler(rule);

        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreSame(replacement, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(LifeReductions(context, target));
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(context, target));
        Assert.IsTrue(context.History.OfType<DamageLifeReduceEvent>()
            .Where(ev => ev.Target == target).All(ev => ev.Source == null));
        Assert.IsTrue(context.History.OfType<ScheduledDamageTickEvent>()
            .All(ev => ReferenceEquals(origin, ev.ApplicationOrigin)));
        Assert.IsTrue(context.History.OfType<DamageEvent>()
            .All(ev => ReferenceEquals(origin, ev.ApplicationOrigin)));
        Assert.IsTrue(context.History.OfType<DamageLifeReduceEvent>()
            .All(ev => ReferenceEquals(origin, ev.ApplicationOrigin)));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(7));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("A higher-level actorless status replaced one pending effect at full quota; old ticks and expiry did not affect two new null-source hits.");
    }

    [TestMethod]
    public void ActorlessStatusDamageRetainsItsRealOriginThroughConfirmedDeath()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        target.Property.LifePoint = 5;
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, null, target, origin, "burn", 1, TimeSpan.FromSeconds(8),
            effects: [Periodic(damage, 1)]);
        context.AdvanceTo(TimeSpan.Zero);
        source.EventReceiver.TryRemoveHandler(rule);

        context.AdvanceTo(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { 5 }, LifeReductions(context, target));
        DeathEvent death = context.History.OfType<DeathEvent>().Single();
        DeathResolvedEvent resolved = context.History.OfType<DeathResolvedEvent>().Single();
        Assert.IsNull(death.Killer);
        Assert.IsNull(resolved.Killer);
        Assert.AreSame(origin, death.ApplicationOrigin);
        Assert.AreSame(origin, resolved.ApplicationOrigin);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsFalse(target.IsAlive);
        TestContext.WriteLine("After its rule was removed, an actorless status dealt five damage and retained the real origin through confirmed death with a null killer.");
    }

    [TestMethod]
    public void ActorlessStatusHealsThroughTheStandardEventChain()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        HealAbility healing = CreateScheduledHeal();
        HealProfile profile = HealProfile.From(healing);
        target.Property.LifePoint = 40;
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "regeneration", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, null, target, origin, "regeneration", 1, TimeSpan.FromSeconds(8),
            effects: [PeriodicHeal(profile, 1)]);
        context.AdvanceTo(TimeSpan.Zero);
        healing.BaseHeal = 50;
        source.EventReceiver.TryRemoveHandler(rule);

        context.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.AreEqual(50, target.Property.LifePoint);
        HealLifeGainEvent[] gains = context.History.OfType<HealLifeGainEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).ToArray();
        CollectionAssert.AreEqual(new[] { 5, 5 }, gains.Select(ev => ev.LifeGain).ToArray());
        Assert.IsTrue(gains.All(ev => ev.Source == null && ReferenceEquals(ev.ApplicationOrigin, origin)));
        Assert.IsTrue(context.History.OfType<HealEvent>().All(ev =>
            ev.Source == null && ReferenceEquals(ev.ApplicationOrigin, origin)));
        Assert.IsTrue(context.History.OfType<ScheduledHealTickEvent>()
            .All(ev => ReferenceEquals(ev.ApplicationOrigin, origin)));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(8));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("Two actorless ticks each healed five through ordinary heal events, retaining the removed rule and the original ability snapshot.");
    }

    [TestMethod]
    public void PeriodicDamageAndHealingShareOneAtomicQuota()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add, maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        HealProfile healing = HealProfile.From(CreateScheduledHeal());
        target.Property.LifePoint = 40;
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "effect", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, null, target, origin, "effect", 1, TimeSpan.FromSeconds(8),
            effects: [Periodic(damage, 3)]);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();

        ApplyStatus(context, null, target, origin, "effect", 2, TimeSpan.FromSeconds(8),
            effects: [Periodic(damage, 3), PeriodicHeal(healing, 1)]);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(PeriodicEffectRejectionReason.Capacity,
            context.History.OfType<StatusApplicationRejectedEvent>().Single().Reason);

        ApplyStatus(context, null, target, origin, "effect", 2, TimeSpan.FromSeconds(8),
            effects: [PeriodicHeal(healing, 1)]);
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        Assert.AreEqual(2, target.EventReceiver.GetHandlers<StatusAbility>().Single().Level);
        Assert.AreEqual(50, target.Property.LifePoint);
        Assert.IsEmpty(LifeReductions(context, target));
        CollectionAssert.AreEqual(new[] { 5, 5 }, context.History.OfType<HealLifeGainEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).Select(ev => ev.LifeGain).ToArray());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.HasCount(1, diagnostics);
        TestContext.WriteLine("Mixed damage and healing exceeded one shared slot; a one-for-one damage-to-healing replacement succeeded without old damage.");
    }

    [TestMethod]
    public void TriggeredStatusStartsItsOwnPeriodicHealing()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        using var projectileCancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        HealAbility healing = CreateScheduledHeal();
        target.Property.LifePoint = 40;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "regeneration";
        listener.PeriodicEffects = [PeriodicHeal(HealProfile.From(healing), 1)];
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Guid grant = Guid.NewGuid();
        var binding = new AtomicTransaction();
        listener.Binding.StageBind(binding, new TestAbilitySource(grant));
        listener.Binding.StageAuthority(binding, context, source.Id);
        binding.Commit();
        ConfigureAppliedStatusFactory(target, "regeneration", AbilityStackPolicy.ExtendOnly);
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver, CancellationTokenSource = projectileCancellation
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.HasCount(1, target.EventReceiver.GetHandlers<StatusAbility>());
        projectileCancellation.Cancel();
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.AreEqual(50, target.Property.LifePoint);
        HealLifeGainEvent[] gains = context.History.OfType<HealLifeGainEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).ToArray();
        CollectionAssert.AreEqual(new[] { 5, 5 }, gains.Select(ev => ev.LifeGain).ToArray());
        Assert.IsTrue(gains.All(ev => ev.Source == source && ev.ApplicationOrigin?.Ability == listener));
        Assert.IsTrue(gains.All(ev => ev.ApplicationOrigin?.AbilitySnapshot?.RuntimeInstanceId == listener.RuntimeInstanceId &&
            ev.ApplicationOrigin.AbilitySnapshot.GrantSourceInstanceId == grant &&
            ev.ApplicationOrigin.AbilitySnapshot.GrantSourceCategory == "Test" &&
            ev.HealAbility.Ability?.RuntimeInstanceId == healing.RuntimeInstanceId));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("A real projectile-triggered status survived cancellation and source uninstall; its two healing ticks retained the original ability and actor.");
    }

    [TestMethod]
    public void OrdinaryHealingSettlesExactlyOnceAndCanRestoreToMaximum()
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        target.Property.LifePoint = 40;
        var healing = new HealAbility { Id = "heal", BaseHeal = 10 };
        var trigger = new TriggerHandler
        {
            Id = "heal.trigger", TargetAbilityId = healing.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, healing, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [healing.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Guid grant = Guid.NewGuid();
        var binding = new AtomicTransaction();
        healing.Binding.StageBind(binding, new TestAbilitySource(grant));
        healing.Binding.StageAuthority(binding, context, source.Id);
        binding.Commit();
        void Hit()
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
                Target = target, Receiver = source.EventReceiver
            })).ToList();
            context.AdvanceTo(TimeSpan.Zero);
        }

        Hit();
        Assert.AreEqual(50, target.Property.LifePoint);
        healing.RestoreToMaximum = true;
        Hit();
        Assert.AreEqual(100, target.Property.LifePoint);
        HealLifeGainEvent[] gains = context.History.OfType<HealLifeGainEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).ToArray();
        CollectionAssert.AreEqual(new[] { 10, 50 }, gains.Select(ev => ev.LifeGain).ToArray());
        Assert.IsTrue(gains.All(ev => ev.Source == source && ev.ApplicationOrigin == null));
        healing.Id = "renamed-after-release";
        healing.BaseHeal = 99;
        var transfer = new AtomicTransaction();
        healing.Binding.StageAuthority(transfer, context, target.Id);
        transfer.Commit();
        Assert.IsTrue(gains.All(ev => ev.HealAbility.Id == "heal" &&
            ev.HealAbility.Ability is { } ability && ability.RuntimeInstanceId == healing.RuntimeInstanceId &&
            ability.GrantSourceInstanceId == grant && ev.HealAbility.Amount == 10));
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = target,
            AbilityIds = [nameof(HealingHandler)], Receiver = target.EventReceiver
        }).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.IsNotNull(target.EventReceiver.Get<HealingHandler>());
        TestContext.WriteLine("Ordinary projectile-triggered healing settled once per hit and restore-to-maximum used the target's current missing life.");
    }

    [TestMethod]
    public void FailedPeriodicPreparationPreservesTheExistingStatusAndEffect()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendAndStack);
        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(8),
            effects: [Periodic(damage, 2)]);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        int installedBefore = context.History.OfType<AbilityInstalledEvent>().Count();

        PeriodicDamageApplication invalid = Periodic(damage, 1) with { TickInterval = TimeSpan.Zero };
        ApplyStatus(context, source, target, origin, "burn", 2, TimeSpan.FromSeconds(10), effects: [invalid]);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));

        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.IsFalse(lease.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(installedBefore, context.History.OfType<AbilityInstalledEvent>().Count());
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(context, target));
        TestContext.WriteLine("Invalid replacement preparation preserved the original status, lease, capacity and both original hits.");
    }

    [TestMethod]
    public void CanceledStatusReplacementPreservesTheOriginalPeriodicEffect()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        using var cancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(8),
            effects: [Periodic(damage, 2)]);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        AbilityLeaseActivatedEvent lease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
        int installedBefore = context.History.OfType<AbilityInstalledEvent>().Count();
        context.EventProcessed += ev =>
        {
            if (ev is AbilityInstallEvent install &&
                install.Handlers.OfType<StatusAbility>().Any(status => status.Level == 2))
                cancellation.Cancel();
        };
        ApplyStatus(context, source, target, origin, "burn", 2, TimeSpan.FromSeconds(10),
            effects: [Periodic(damage, 1)], cancellation: cancellation);

        context.AdvanceTo(TimeSpan.Zero);

        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.IsFalse(lease.LeaseCancellation.IsCancellationRequested);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(installedBefore, context.History.OfType<AbilityInstalledEvent>().Count());
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(context, target));
        TestContext.WriteLine("Cancellation before a higher-level commit left the original status, lease and both scheduled hits intact.");
    }

    [TestMethod]
    public void PeriodicReplacementCleanupFailureReportsTheCommittedStatus()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        CancellationTokenRegistration registration = default;
        bool registered = false;
        context.EventQueued += ev =>
        {
            if (!registered && ev is AbilityLifecycleEvent { RemainingTicks: > 0 } lifecycle)
            {
                registered = true;
                registration = lifecycle.CancellationToken.Register(
                    () => throw new InvalidOperationException("periodic cleanup observer failed"));
            }
        };
        try
        {
            ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(5),
                effects: [Periodic(damage, 2)]);
            context.AdvanceTo(TimeSpan.Zero);
            StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
            AbilityLeaseActivatedEvent oldLease = context.History.OfType<AbilityLeaseActivatedEvent>().Single();
            ApplyStatus(context, source, target, origin, "burn", 2, TimeSpan.FromSeconds(8),
                effects: [Periodic(damage, 4)]);

            var failure = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
            StringAssert.Contains(failure.ToString(), "periodic cleanup observer failed");
            StatusAbility replacement = target.EventReceiver.GetHandlers<StatusAbility>().Single();
            Assert.AreNotSame(original, replacement);
            Assert.AreEqual(2, replacement.Level);
            Assert.AreEqual(120, target.Property.LifePointMax);
            Assert.IsTrue(oldLease.LeaseCancellation.IsCancellationRequested);
            Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
            TestContext.WriteLine("A retirement callback failed after commit; the replacement status and periodic reservation remained published.");
        }
        finally
        {
            registration.Dispose();
        }
    }

    [TestMethod]
    public void ExpiryAndManualUninstallRemoveOwnedPeriodicEffects()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        var origin = StatusApplicationOrigin.FromRule(rule, source.EventReceiver);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.Independent);
        ApplyStatus(context, source, target, origin, "burn", 1, TimeSpan.FromSeconds(2),
            effects: [Periodic(damage, 2)]);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));

        ApplyStatus(context, source, target, origin, "burn", 1, null,
            effects: [Periodic(damage, 10)]);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        StatusAbility mounted = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = target,
            AbilityIds = [mounted.Id], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(15));
        Assert.IsEmpty(LifeReductions(context, target));
        TestContext.WriteLine("Timed expiry and manual uninstall each atomically removed the status and its pending effect before any damage.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancelingFollowingLeaseRemovesStatusAndPeriodicEffect(bool timed)
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        using var cancellation = new CancellationTokenSource();
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, source, target, StatusApplicationOrigin.FromRule(rule, source.EventReceiver),
            "burn", 1, timed ? TimeSpan.FromSeconds(8) : null,
            effects: [Periodic(damage, 2)], cancellation: cancellation);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.HasCount(1, target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(context));

        Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
        context.AdvanceTo(TimeSpan.Zero);

        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.HasCount(1, context.History.OfType<AbilityUninstalledEvent>()
            .Where(ev => ev.Target == target));
        context.AdvanceTo(TimeSpan.FromSeconds(10));
        Assert.IsEmpty(LifeReductions(context, target));
        TestContext.WriteLine($"Canceled {(timed ? "timed" : "untimed")} lease removed the status, property and pending ticks.");
    }

    [TestMethod]
    public void PeriodicSettlementIsNotDuplicatedByOtherTickTriggers()
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        var extra = new TriggerHandler
        {
            Id = "additional-tick-trigger", TargetAbilityId = damage.Id,
            Config = new AbilityTriggerConfig
            { Type = TriggerType.ScheduledDamageTick, EventSourceScope = TriggerSourceScope.Any }
        };
        TestHandlerFactory.Register(target, extra);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target,
            AbilityIds = [extra.Id], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, source, target, StatusApplicationOrigin.FromRule(rule, source.EventReceiver),
            "burn", 1, TimeSpan.FromSeconds(5), effects: [Periodic(damage, 1)]);

        context.AdvanceTo(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(context, target));
        Assert.HasCount(2, target.EventReceiver.History.OfType<AbilityTriggerInvokeEvent>()
            .Where(ev => ev.UpstreamEvent is ScheduledDamageTickEvent && ev.TargetRuntimeInstanceId == null));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("A second tick observer did not duplicate the two scheduled damage settlements.");
    }

    [TestMethod]
    public void PeriodicHealingIsNotDuplicatedByOtherTickTriggers()
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        target.Property.LifePoint = 40;
        HealProfile heal = HealProfile.From(CreateScheduledHeal());
        var extra = new TriggerHandler
        {
            Id = "additional-heal-trigger", TargetAbilityId = heal.Id,
            Config = new AbilityTriggerConfig
            { Type = TriggerType.ScheduledHealTick, EventSourceScope = TriggerSourceScope.Any }
        };
        TestHandlerFactory.Register(target, extra);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target,
            AbilityIds = [extra.Id], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        ConfigureAppliedStatusFactory(target, "regeneration", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, source, target, StatusApplicationOrigin.FromRule(rule, source.EventReceiver),
            "regeneration", 1, TimeSpan.FromSeconds(5), effects: [PeriodicHeal(heal, 1)]);

        context.AdvanceTo(TimeSpan.FromSeconds(3));

        Assert.AreEqual(50, target.Property.LifePoint);
        Assert.HasCount(2, target.EventReceiver.History.OfType<AbilityTriggerInvokeEvent>()
            .Where(ev => ev.UpstreamEvent is ScheduledHealTickEvent && ev.TargetRuntimeInstanceId == null));
        CollectionAssert.AreEqual(new[] { 5, 5 }, context.History.OfType<HealLifeGainEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).Select(ev => ev.LifeGain).ToArray());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("A second tick observer did not duplicate the two scheduled healing settlements.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidPeriodicSettlementInfrastructureRejectsTheWholeStatus(bool missingDamageReceiver)
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        DamageProfile damage = ConfigureScheduledDamage(context, target);
        if (missingDamageReceiver)
        {
            context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = target, Target = target,
                AbilityIds = ["receiver"], Receiver = target.EventReceiver
            }).ToList();
            context.AdvanceTo(TimeSpan.Zero);
        }
        else
        {
            target.EventReceiver.AddHandler(new ScheduledDamageHandler());
        }
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        int installedBefore = context.History.OfType<AbilityInstalledEvent>().Count();
        ApplyStatus(context, source, target, StatusApplicationOrigin.FromRule(rule, source.EventReceiver),
            "burn", 1, TimeSpan.FromSeconds(5), effects: [Periodic(damage, 1)]);

        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));

        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(installedBefore, context.History.OfType<AbilityInstalledEvent>().Count());
        TestContext.WriteLine($"Missing damage receiver={missingDamageReceiver}: invalid infrastructure rejected before the status transaction committed.");
    }

    [TestMethod]
    public void ActorlessRuleEffectsRetainRealOriginWithoutInventingAnAbility()
    {
        var factory = new TriggerChainFactory();
        (Context context, Unit source, Unit target) = CreateUnits(triggerFactory: factory);
        using var cleanup = context;
        context.Logger.Output = null;
        target.Property.LifePoint = 40;
        var schema = new ExpressionSchema<CombatFormulaContext>();
        schema.RegisterNumber("effect.level", value => value.AbilityLevel);
        schema.Seal();
        var types = new DamageTypeRegistry([new DamageTypeDefinition("impact")]);
        var definition = new DamageDefinition(types, "impact",
            NumericFormula<CombatFormulaContext>.Compile("effect.level * 5", schema));
        DamageProfile damage = DamageProfile.ForEffect("environment.burn", 1, [definition], FormulaEvaluationPolicy.Strict);
        HealProfile healing = HealProfile.ForEffect("environment.rain", 5);
        var receiver = new BeingDamageAbility { Id = "receiver", DamageTypes = types };
        TestHandlerFactory.Register(target, receiver);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target,
            AbilityIds = [receiver.Id], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        var rule = new TestRule("weather");
        source.EventReceiver.AddHandler(rule);
        ConfigureAppliedStatusFactory(target, "weather-status", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(context, null, target, StatusApplicationOrigin.FromRule(rule, source.EventReceiver),
            "weather-status", 1, TimeSpan.FromSeconds(5),
            effects: [Periodic(damage, 1), PeriodicHeal(healing, 1)]);

        context.AdvanceTo(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(context, target));
        HealLifeGainEvent[] gains = context.History.OfType<HealLifeGainEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).ToArray();
        CollectionAssert.AreEqual(new[] { 5, 5 }, gains.Select(ev => ev.LifeGain).ToArray());
        DamageEvent[] damages = context.History.OfType<DamageEvent>()
            .Where(ev => ev.Receiver == target.EventReceiver).ToArray();
        Assert.HasCount(2, damages);
        Assert.IsTrue(damages.All(ev => ev.Source == null && ev.DamageAbilityId == null &&
            ev.AbilitySnapshot == null && ev.ApplicationOrigin?.Rule == rule));
        Assert.IsTrue(gains.All(ev => ev.Source == null && ev.HealAbilityId == null &&
            ev.HealAbility.Ability == null && ev.ApplicationOrigin?.Rule == rule));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DamageProfile.ForEffect("bad", 0, [definition], FormulaEvaluationPolicy.Strict));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HealProfile.ForEffect("bad", -1));
        TestContext.WriteLine("Actorless rule damage and healing settled twice with the actual rule origin and no fabricated ability.");
    }

    [TestMethod]
    public void AStatusCannotReplaceAnotherContextsPeriodicEffect()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        (Context first, Unit firstSource, Unit firstTarget) = CreateUnits(triggerFactory: factory);
        using var firstCleanup = first;
        first.Logger.Output = null;
        DamageProfile firstDamage = ConfigureScheduledDamage(first, firstTarget);
        var firstRule = new TestRule("first");
        firstSource.EventReceiver.AddHandler(firstRule);
        ConfigureAppliedStatusFactory(firstTarget, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(first, firstSource, firstTarget, StatusApplicationOrigin.FromRule(firstRule, firstSource.EventReceiver),
            "burn", 1, null, effects: [Periodic(firstDamage, 2)]);
        first.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle foreign = first.History.OfType<PeriodicEffectStartedEvent>().Single().Effect;

        (Context second, Unit secondSource, Unit secondTarget) = CreateUnits(triggerFactory: factory);
        using var secondCleanup = second;
        second.Logger.Output = null;
        DamageProfile secondDamage = ConfigureScheduledDamage(second, secondTarget);
        var secondRule = new TestRule("second");
        secondSource.EventReceiver.AddHandler(secondRule);
        ConfigureAppliedStatusFactory(secondTarget, "burn", AbilityStackPolicy.ExtendOnly);
        ApplyStatus(second, secondSource, secondTarget,
            StatusApplicationOrigin.FromRule(secondRule, secondSource.EventReceiver),
            "burn", 1, null, effects: [Periodic(secondDamage, 1) with { Replaces = foreign }]);
        Assert.ThrowsExactly<InvalidOperationException>(() => second.AdvanceTo(TimeSpan.Zero));

        Assert.IsEmpty(secondTarget.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(second));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(first));
        first.AdvanceTo(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(new[] { 5, 5 }, LifeReductions(first, firstTarget));
        TestContext.WriteLine("A foreign periodic handle rejected the new status without changing either context or the original two hits.");
    }

    [TestMethod]
    public void MapOriginRemainsRecordedAfterRemovalAndInvalidOriginsAreRejected()
    {
        (Context context, Unit source, Unit target) = CreateUnits();
        using var cleanup = context;
        ConfigureAppliedStatusFactory(target, "burn", AbilityStackPolicy.ExtendOnly);
        var map = new MapRuntime();
        var mapObject = new MapObject("trap", new Vector2D(3, 3), source.EventReceiver);
        map.AddObject(mapObject);
        var origin = StatusApplicationOrigin.FromMapObject(mapObject);
        ApplyStatus(context, null, target, origin, "burn", 1, TimeSpan.FromSeconds(3));
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility applied = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        map.RemoveObject(mapObject);
        Assert.AreSame(mapObject, applied.ApplicationOrigin!.MapObject);
        Assert.IsNull(applied.ApplicationSource);
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());

        ApplyStatus(context, null, target, origin, "burn", 1, null);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(3)));
        ApplyStatus(context, source, target, null, "burn", 1, null);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(3)));

        (Context foreignContext, Unit foreignSource, Unit _) = CreateUnits();
        using var foreignCleanup = foreignContext;
        var foreignRule = new TestRule("foreign");
        foreignSource.EventReceiver.AddHandler(foreignRule);
        var foreignOrigin = StatusApplicationOrigin.FromRule(foreignRule, foreignSource.EventReceiver);
        ApplyStatus(context, null, target, foreignOrigin, "burn", 1, null);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(3)));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        TestContext.WriteLine("A removed map object remained in its existing status snapshot; removed, missing and cross-context origins could not apply new statuses.");
    }

    private sealed class TestRule(string id) : IHandler
    {
        public string Id { get; set; } = id;
    }

    private sealed record PeriodicPreparationProbeEvent : Event;

    private sealed class PeriodicPreparationProbe(
        Unit source, IReadOnlyList<Unit> targets, StatusApplicationOrigin origin,
        PeriodicEffectApplication application, IReadOnlySet<Guid>? removed = null) : IHandler
    {
        public string Id { get; set; } = nameof(PeriodicPreparationProbe);
        public PeriodicEffectRejectionReason Rejection { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPrepare(PeriodicPreparationProbeEvent ev)
        {
            var chain = ev.Causality as AbilityTriggerChain ??
                throw new InvalidOperationException("The probe requires a trigger chain.");
            var transaction = new AtomicTransaction();
            var prepared = new List<PeriodicEffectRegistry.PreparedChanges>();
            try
            {
                if (removed is { Count: > 0 })
                {
                    PeriodicEffectRegistry.PreparedChanges retirement =
                        chain.PeriodicEffects.PrepareChanges(transaction, chain, null, targets[0], [], removed,
                            null, new HashSet<Guid>(), null, default, out _, out _) ??
                        throw new InvalidOperationException("Periodic retirement was unexpectedly rejected.");
                    prepared.Add(retirement);
                }
                foreach (Unit target in targets)
                {
                    PeriodicEffectRegistry.PreparedChanges? change = chain.PeriodicEffects.PrepareChanges(
                        transaction, chain, source, target, [application], new HashSet<Guid>(), Guid.NewGuid(),
                        new HashSet<Guid>(), origin, default, out var rejection, out _);
                    if (change == null)
                    {
                        Rejection = rejection;
                        foreach (var prior in prepared) prior.Abort();
                        yield break;
                    }
                    prepared.Add(change);
                }
                transaction.Commit();
            }
            catch
            {
                foreach (var prior in prepared) prior.Abort();
                throw;
            }

            Guid operationId = Guid.NewGuid();
            ev.Context.MarkCurrentOperationCommitted(operationId);
            foreach (var change in prepared) change.Publish();
            Exception[] failures = prepared.SelectMany(change => change.CloseRetired()).ToArray();
            if (failures.Length > 0)
                throw new AggregateException("Retired periodic effects failed to close.", failures);
            foreach (var change in prepared)
                foreach (var effect in change.Added)
                    foreach (Event start in PeriodicEffectRegistry.CreateStartEvents(
                        ev.Context, operationId, source, effect.Handle.Target, null, effect.Application,
                        effect.Handle, operationId))
                        yield return start;
        }
    }

    private sealed class PeriodicStatusBatchProbe(
        Unit source, IReadOnlyList<PeriodicEffectRegistry.StatusChange> changes) : IHandler
    {
        public string Id { get; set; } = nameof(PeriodicStatusBatchProbe);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPrepare(PeriodicPreparationProbeEvent ev)
        {
            var chain = ev.Causality as AbilityTriggerChain ??
                throw new InvalidOperationException("The probe requires a trigger chain.");
            var transaction = new AtomicTransaction();
            PeriodicEffectRegistry.PreparedChanges prepared =
                chain.PeriodicEffects.PrepareStatusBatch(transaction, chain, source, changes, default,
                    out _, out _) ?? throw new InvalidOperationException("The probe batch was rejected.");
            try { transaction.Commit(); }
            catch
            {
                prepared.Abort();
                throw;
            }
            Guid operationId = Guid.NewGuid();
            ev.Context.MarkCurrentOperationCommitted(operationId);
            prepared.Publish();
            IReadOnlyList<Exception> failures = prepared.CloseRetired();
            if (failures.Count > 0) throw new AggregateException("Retired effects failed to close.", failures);
            foreach (var effect in prepared.Added)
                foreach (Event start in PeriodicEffectRegistry.CreateStartEvents(
                    ev.Context, operationId, source, effect.Handle.Target, null, effect.Application,
                    effect.Handle, operationId))
                    yield return start;
        }
    }

    private sealed class FixedTargetsSelector(params Unit[] targets) : ITargetSelector
    {
        public TargetingOrigin Origin { get; set; }
        public bool Validate(AbilityTargetingContext context) => true;
        public IReadOnlyList<Unit> ResolveTargets(AbilityTargetingContext context, UnitSpatialRegistry registry) => targets;
    }

    private static (Context Context, TriggerChainFactory Factory, Unit Source, Unit First, Unit Second, StatusAbility Listener)
        CreateGatedStatusBatch(TimeProvider? clock = null)
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 2);
        (Context context, Unit source, Unit first) = CreateUnits(clock, factory);
        context.Logger.Output = null;
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(second.EventReceiver);
        source.Registry = new UnitSpatialRegistry();
        first.Property.LifePoint = 40;
        second.Property.LifePoint = 40;
        var listener = CreateStatus("listener", AbilityStackPolicy.ExtendOnly);
        listener.IsTriggerListener = true;
        listener.PayloadId = "regeneration";
        listener.Selector = new FixedTargetsSelector(first, second);
        listener.PeriodicEffects = [PeriodicHeal(HealProfile.From(CreateScheduledHeal()), 1)];
        listener.GateManager = new CommandGateManager
        {
            Charges = new AbilityChargeConfiguration(1, 1), ResourceCost = 5,
            Cooldown = TimeSpan.FromSeconds(3), OccupiesWindup = false
        };
        var trigger = new TriggerHandler
        {
            Id = "listener.trigger", TargetAbilityId = listener.Id,
            TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["projectile"]
        };
        TestHandlerFactory.Register(source, listener, trigger);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = source,
            AbilityIds = [listener.Id, trigger.Id], Receiver = source.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        source.Property.EnergyRecoveryPerSecond = 0;
        source.Abilities.BaseProperty.EnergyRecoveryPerSecond = 0;
        ConfigureAppliedStatusFactory(first, "regeneration", AbilityStackPolicy.ExtendOnly);
        ConfigureAppliedStatusFactory(second, "regeneration", AbilityStackPolicy.ExtendOnly);
        return (context, factory, source, first, second, listener);
    }

    private static void DispatchStatusHit(Context context, Unit source, Unit target) =>
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "projectile", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();

    private static void ConfigureAppliedStatusFactory(Unit target, string id, AbilityStackPolicy policy)
    {
        AbilityInstallHandler installer = target.EventReceiver.Get<AbilityInstallHandler>()
            ?? throw new InvalidOperationException("The unit has no installer.");
        installer.AbilityFactory = (requested, level) =>
        {
            if (!string.Equals(requested, id, StringComparison.Ordinal))
                throw new InvalidOperationException("Unknown test status.");
            StatusAbility status = CreateStatus(id, policy);
            status.IsTriggerListener = true;
            status.PayloadId = id;
            status.Level = level ?? 1;
            status.Labels |= AbilityLabel.Status;
            status.Modifiers = [Modifier(PropertyModifierTargets.LifePointMax, "ability.level * 10")];
            return [status];
        };
    }

    private static Guid ApplyStatus(
        Context context, Unit? source, Unit target, StatusApplicationOrigin? origin, string id,
        int level, TimeSpan? expireAt, TimeSpan delay = default,
        IReadOnlyList<PeriodicEffectApplication>? effects = null, CancellationTokenSource? cancellation = null)
    {
        Guid commandId = Guid.NewGuid();
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = commandId, Source = source, Target = target, AbilityIds = [id],
            Level = level, Purpose = AbilityInstallPurpose.StatusApplication,
            ApplicationOrigin = origin, ExpireAt = expireAt, Receiver = target.EventReceiver,
            TimeSpan = delay, PeriodicEffects = effects ?? [], CancellationTokenSource = cancellation
        }).ToList();
        return commandId;
    }

    private static DamageProfile ConfigureScheduledDamage(Context context, Unit target)
    {
        var schema = new ExpressionSchema<CombatFormulaContext>();
        schema.RegisterNumber("ability.level", value => value.AbilityLevel);
        schema.Seal();
        var types = new DamageTypeRegistry([new DamageTypeDefinition("impact")]);
        var ability = new DamageAbility
        {
            Id = "periodic-damage", Level = 1,
            Damages = [new DamageDefinition(types, "impact",
                NumericFormula<CombatFormulaContext>.Compile("ability.level * 5", schema))]
        };
        var receiver = new BeingDamageAbility { Id = "receiver", DamageTypes = types };
        var death = new DeathAbility { Id = "death" };
        TestHandlerFactory.Register(target, receiver, death);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target,
            AbilityIds = [receiver.Id, death.Id], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        return DamageProfile.From(ability);
    }

    private static PeriodicDamageApplication Periodic(DamageProfile damage, int firstTickSeconds) => new()
    {
        Damage = damage, TickCount = 2, TickInterval = TimeSpan.FromSeconds(1),
        FirstTickDelay = TimeSpan.FromSeconds(firstTickSeconds)
    };

    private static HealAbility CreateScheduledHeal() => new() { Id = "periodic-heal", BaseHeal = 5 };

    private static PeriodicHealApplication PeriodicHeal(HealProfile healing, int firstTickSeconds) => new()
    {
        Heal = healing, TickCount = 2, TickInterval = TimeSpan.FromSeconds(1),
        FirstTickDelay = TimeSpan.FromSeconds(firstTickSeconds)
    };

    private static int[] LifeReductions(Context context, Unit target) =>
        context.History.OfType<DamageLifeReduceEvent>().Where(ev => ev.Receiver == target.EventReceiver)
            .Select(ev => ev.LifePointReduce).ToArray();

    private static PropertyModifier Modifier(PropertyModifierTarget target, string formula, string? fallback = null)
    {
        var schema = new ExpressionSchema<PropertyFormulaContext>();
        schema.RegisterNumber("ability.level", value => value.AbilityLevel);
        schema.RegisterNumber("target.level", value => value.TargetProperty.Level);
        schema.RegisterNumber("target.energy", value => (double)value.TargetProperty.Energy);
        schema.Seal();
        return new PropertyModifier(target, NumericFormula<PropertyFormulaContext>.Compile(formula, schema, fallback));
    }

    private static void DispatchInstall(
        Context context, Unit source, Unit target, StatusAbility status, TimeSpan? expireAt,
        TimeSpan delay, CancellationTokenSource? token = null)
        => DispatchInstall(context, source, target, new[] { status }, expireAt, delay, token);

    private static void DispatchInstall(
        Context context, Unit source, Unit target, IReadOnlyList<StatusAbility> statuses, TimeSpan? expireAt,
        TimeSpan delay, CancellationTokenSource? token = null)
    {
        TestHandlerFactory.Register(target, statuses.ToArray());
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            Source = source,
            Target = target,
            AbilityIds = statuses.Select(status => status.Id).ToArray(),
            ExpireAt = expireAt,
            Receiver = target.EventReceiver,
            TimeSpan = delay,
            CancellationTokenSource = token,
        }).ToList();
    }

    private static StatusAbility CreateStatus(string id, AbilityStackPolicy policy)
    {
        return new StatusAbility
        {
            Id = id,
            Duration = TimeSpan.FromSeconds(5),
            Stack = 3,
            StackPolicy = policy,
            Labels = AbilityLabel.Buff | AbilityLabel.WeakDispellable,
            Modifiers = new List<PropertyModifier>(),
        };
    }

    private static void Install(Context context, Unit source, Unit target, StatusAbility status, TimeSpan at)
    {
        TestHandlerFactory.Register(target, status);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            Source = source,
            Target = target,
            AbilityIds = new[] { status.Id },
            ExpireAt = at.Add(status.Duration).Subtract(TimeSpan.FromTicks(1)),
            Receiver = target.EventReceiver,
            TimeSpan = at,
        }).ToList();
    }

    private static (Context Context, Unit Source, Unit Target) CreateUnits(
        TimeProvider? timeProvider = null, TriggerChainFactory? triggerFactory = null)
    {
        Context context = new Context(timeProvider: timeProvider, causalityFactory: triggerFactory);
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        return (context, source, target);
    }

    private static Unit CreateUnit(Context context, string id)
    {
        UnitProperty property = new UnitProperty
        {
            LifePoint = 100,
            LifePointMax = 100,
            MoveSpeed = 300,
            Level = 1,
        };

        Unit unit = new Unit
        {
            Name = id,
            ArchetypeId = id,
            Property = property,
            Abilities = new UnitAbilityManager(),
            EventReceiver = context.Factory.NewEventReceiver(),
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler(), new AbilityLifecycleHandler());
        unit.Initialize();
        return unit;
    }
}
