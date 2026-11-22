using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void ExperienceRewardAbilityCompilesFromConfirmedDeathContent()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            damageType: [{ id: default }]
            ability:
              - id: reward
                type: ExperienceReward
                allowMultiple: false
                trigger: { type: Death }
                data: { amount: 25, victimTags: [dummy], killerTags: [player] }
            """);

        ExperienceRewardAbility reward = catalog.Require("reward").CreateHandlers(1)
            .OfType<ExperienceRewardAbility>().Single();
        Assert.AreEqual(25, reward.Amount);
        CollectionAssert.AreEqual(new[] { "dummy" }, reward.RequiredVictimTags.ToArray());
        CollectionAssert.AreEqual(new[] { "player" }, reward.RequiredKillerTags.ToArray());
    }

    [TestMethod]
    public void SourceCompetitionSelectsActualLevelRetainsTiesAndRestoresOriginalCandidates()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "ability.level * 10" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit unit = CreateSourceUnit(context, catalog, "owner");
        var first = new AbilitySourceInstance("Equipment", [new("endurance", 1)]);
        var second = new AbilitySourceInstance("Talent", [new("endurance", 5)]);
        ChangeSources(context, unit, [new(first, unit)]);
        AbilityBase original = first.Abilities.Single();
        Guid runtimeId = original.RuntimeInstanceId;
        Assert.AreEqual(110, unit.Property.LifePointMax);
        ChangeSources(context, unit, [new(second, unit)]);
        AbilityBase stronger = second.Abilities.Single();
        Assert.AreEqual(150, unit.Property.LifePointMax);
        Assert.AreSame(stronger, unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreSame(unit, first.Holder);
        Assert.AreSame(original, first.Abilities.Single());
        ChangeSources(context, unit, levels: [new(second, stronger.RuntimeInstanceId, 1)]);
        Assert.AreSame(stronger, unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, unit.Property.LifePointMax);
        ChangeSources(context, unit, levels: [new(first, runtimeId, 2)]);
        Assert.AreSame(original, unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(120, unit.Property.LifePointMax);
        Assert.AreEqual(runtimeId, original.RuntimeInstanceId);
        Assert.ThrowsExactly<InvalidOperationException>(() => original.Level = 8);
        Assert.ThrowsExactly<InvalidOperationException>(() => original.AllowMultiple = true);
        Assert.ThrowsExactly<InvalidOperationException>(() => unit.Abilities.RemoveByAbilityIds(["endurance"]));
        ChangeSources(context, unit, [new(first, null)]);
        Assert.AreSame(stronger, unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, unit.Property.LifePointMax);
        ChangeSources(context, unit, [new(first, unit), new(second, null)]);
        Assert.AreSame(original, unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(120, unit.Property.LifePointMax);
        Assert.AreEqual(1, unit.Property.Level);
        TestContext.WriteLine("Real source instances competed at levels 1/5, retained the current equal-level winner, upgraded in place and restored the original object.");
    }

    [TestMethod]
    public void CoexistingSourcePropertiesStackAndUnmountBySourceNotDefinition()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: true
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "10" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit unit = CreateSourceUnit(context, catalog, "owner");
        var first = new AbilitySourceInstance("Equipment", [new("endurance"), new("endurance")]);
        var second = new AbilitySourceInstance("Equipment", [new("endurance")]);
        ChangeSources(context, unit, [new(first, unit), new(second, unit)]);
        Assert.AreEqual(130, unit.Property.LifePointMax);
        Assert.HasCount(3, unit.EventReceiver.GetHandlers<PropertyAbility>());
        Assert.AreNotSame(first.Abilities[0], first.Abilities[1]);
        ChangeSources(context, unit, [new(first, null)]);
        Assert.AreEqual(110, unit.Property.LifePointMax);
        Assert.AreSame(second.Abilities.Single(), unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        ChangeSources(context, unit, [new(first, unit)]);
        Assert.AreEqual(130, unit.Property.LifePointMax);
        TestContext.WriteLine("Three same-definition +10 instances added +30; removing one source removed only its two instances, which remounted unchanged.");
    }

    [TestMethod]
    public void SourceSwapAndUnilateralTransferPreserveRealObjectsAndLevels()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "ability.level * 10" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        var first = new AbilitySourceInstance("Equipment", [new("endurance", 1)]);
        var second = new AbilitySourceInstance("Equipment", [new("endurance", 5)]);
        ChangeSources(context, firstOwner, [new(first, firstOwner), new(second, secondOwner)]);
        AbilityBase firstAbility = first.Abilities.Single();
        AbilityBase secondAbility = second.Abilities.Single();
        ChangeSources(context, firstOwner, [new(first, secondOwner), new(second, firstOwner)]);
        Assert.AreSame(secondAbility, firstOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreSame(firstAbility, secondOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(150, firstOwner.Property.LifePointMax);
        Assert.AreEqual(110, secondOwner.Property.LifePointMax);
        Assert.AreEqual(1, firstAbility.Level);
        Assert.AreEqual(5, secondAbility.Level);
        ChangeSources(context, firstOwner, [new(second, secondOwner)]);
        Assert.IsEmpty(firstOwner.EventReceiver.GetHandlers<PropertyAbility>());
        Assert.AreSame(secondAbility, secondOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.HasCount(2, secondOwner.Abilities.Sources);
        Assert.AreSame(firstAbility, first.Abilities.Single());
        TestContext.WriteLine("Two sources exchanged their original level-1/5 abilities; a subsequent one-way transfer created no replacement object.");
    }

    [TestMethod]
    public void OnlyTheSelectedSourceObservesConditionalRecoveryAndOffSourceTimersContinue()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: pulse
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
                data:
                  gate:
                    charges:
                      maximum: 3
                      initial: 0
                      recovery: { intervalSeconds: 10, mode: Serial }
                      conditionalRecovery:
                        - amount: 1
                          trigger: { type: Signal, signalId: recharge, eventSourceScope: Any }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var lower = new AbilitySourceInstance("Equipment", [new("pulse", 1)]);
        var higher = new AbilitySourceInstance("Equipment", [new("pulse", 5)]);
        ChangeSources(context, owner, [new(lower, owner), new(higher, owner)]);
        var low = (ProjectileAbility)lower.Abilities.Single();
        var high = (ProjectileAbility)higher.Abilities.Single();
        context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            CommandEventId = Guid.NewGuid(), SignalId = "recharge", Source = null, Target = owner,
            Receiver = owner.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(0, low.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(1, high.GateManager!.GetChargeSnapshot()!.Available);
        ChangeSources(context, owner, [new(higher, null)]);
        Assert.AreSame(low, owner.EventReceiver.GetHandlers<ProjectileAbility>().Single());
        context.AdvanceTo(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, low.GateManager.GetChargeSnapshot()!.Available);
        Assert.AreEqual(2, high.GateManager.GetChargeSnapshot()!.Available);
        ChangeSources(context, owner, [new(higher, owner)]);
        Assert.AreSame(high, owner.EventReceiver.GetHandlers<ProjectileAbility>().Single());
        Assert.AreEqual(2, high.GateManager.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("Only the selected source restored on a signal; both the unselected and unmounted original gates retained their serial timer progress.");
    }

    [TestMethod]
    public void UnheldSourceMaterializesOnceAndStartsItsMissingChargeTimers()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: pulse
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
                data:
                  gate:
                    charges: { maximum: 2, initial: 0, recovery: { intervalSeconds: 1, mode: Parallel } }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var source = new AbilitySourceInstance("Equipment", [new("pulse")]);
        ChangeSources(context, owner, materialize: [source]);
        ProjectileAbility ability = (ProjectileAbility)source.Abilities.Single();
        Guid id = ability.RuntimeInstanceId;
        Assert.IsNull(source.Holder);
        Assert.IsEmpty(owner.EventReceiver.GetHandlers<ProjectileAbility>());
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, ability.GateManager!.GetChargeSnapshot()!.Available);
        ChangeSources(context, owner, [new(source, owner)]);
        Assert.AreSame(ability, owner.EventReceiver.GetHandlers<ProjectileAbility>().Single());
        Assert.AreEqual(id, ability.RuntimeInstanceId);
        TestContext.WriteLine("An unheld source entered the context, recovered its missing charges, and later mounted the exact original object.");
    }

    [TestMethod]
    public void FailedSourceProjectionRollsBackOwnershipSelectionAndAbilityLevel()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "10 / (3 - ability.level)" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var original = new AbilitySourceInstance("Equipment", [new("endurance", 1)]);
        ChangeSources(context, owner, [new(original, owner)]);
        AbilityBase ability = original.Abilities.Single();
        var incoming = new AbilitySourceInstance("Equipment", [new("endurance", 3)]);
        Assert.ThrowsExactly<Miaosoft.Sim.Combat.Skill.Expression.ExpressionEvaluationException>(() =>
            ChangeSources(context, owner, [new(original, null), new(incoming, owner)]));
        Assert.AreSame(owner, original.Holder);
        Assert.IsNull(incoming.Holder);
        Assert.IsFalse(incoming.IsMaterialized);
        Assert.AreSame(ability, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(105, owner.Property.LifePointMax);
        Assert.ThrowsExactly<Miaosoft.Sim.Combat.Skill.Expression.ExpressionEvaluationException>(() =>
            ChangeSources(context, owner, levels: [new(original, ability.RuntimeInstanceId, 3)]));
        Assert.AreEqual(1, ability.Level);
        Assert.AreEqual(105, owner.Property.LifePointMax);
        TestContext.WriteLine("A failing replacement and a failing in-place level change preserved the original source, handler, level and projection.");
    }

    [TestMethod]
    public void SourceTransferAcrossContextsIsRejectedWithoutChangingEitherContext()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "10" }] }
            """);
        using var first = new Context(causalityFactory: new TriggerChainFactory());
        using var second = new Context(causalityFactory: new TriggerChainFactory());
        first.Logger.Output = second.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(first, catalog, "first");
        Unit secondOwner = CreateSourceUnit(second, catalog, "second");
        var source = new AbilitySourceInstance("Equipment", [new("endurance")]);
        ChangeSources(first, firstOwner, [new(source, firstOwner)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => ChangeSources(second, secondOwner, [new(source, secondOwner)]));
        Assert.AreSame(firstOwner, source.Holder);
        Assert.AreEqual(110, firstOwner.Property.LifePointMax);
        Assert.AreEqual(100, secondOwner.Property.LifePointMax);
        Assert.IsEmpty(secondOwner.Abilities.Sources);
        TestContext.WriteLine("A real source could not be moved into a second context; the original owner and both projections remained intact.");
    }

    [TestMethod]
    [DataRow("install-request")]
    [DataRow("install")]
    [DataRow("uninstall-request")]
    [DataRow("uninstall")]
    [DataRow("atomic-remove")]
    [DataRow("atomic-add")]
    public void ManagedSourceCannotBeCorruptedThroughDefinitionOrBareAtomicChanges(string operation)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "10" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var source = new AbilitySourceInstance("Equipment", [new("endurance")]);
        ChangeSources(context, owner, [new(source, owner)]);
        AbilityBase ability = source.Abilities.Single();
        Event request = operation switch
        {
            "install-request" => context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                AbilityIds = ["endurance"], Receiver = owner.EventReceiver
            },
            "install" => context.Factory.NewEvent<AbilityInstallEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                Handlers = catalog.Require("endurance").CreateHandlers(1), Receiver = owner.EventReceiver
            },
            "uninstall-request" => context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                AbilityIds = ["endurance"], Receiver = owner.EventReceiver
            },
            "uninstall" => context.Factory.NewEvent<AbilityUninstallEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                AbilityIds = ["endurance"], Receiver = owner.EventReceiver
            },
            _ => context.Factory.NewEvent<AtomicAbilityChangeEvent>() with
            {
                Target = owner, OperationId = Guid.NewGuid(), Transaction = new AtomicTransaction(),
                Changes = [new(owner, operation == "atomic-remove" ? [ability] : [],
                    operation == "atomic-add" ? [ability] : [])],
                Completion = context.Factory.NewEvent<AbilitySourcesChangedEvent>() with
                {
                    OperationId = Guid.NewGuid(), Sources = [source], Receiver = owner.EventReceiver
                },
                Receiver = owner.EventReceiver
            }
        };
        context.Dispatch(request).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(context.ContextTimeSpan));
        Assert.AreSame(owner, source.Holder);
        Assert.AreSame(ability, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, owner.Property.LifePointMax);
        TestContext.WriteLine($"Rejected {operation} without changing the registered source or projection.");
    }

    [TestMethod]
    public void SourceFactoryCannotReuseMountedPartsEvenWhenMaterializingWithoutAHolder()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "10" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        InstallCatalog(context, owner, catalog, "endurance", 1);
        context.AdvanceTo(TimeSpan.Zero);
        AbilityBase original = owner.EventReceiver.GetHandlers<PropertyAbility>().Single();
        owner.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory = (_, _) => [original];
        var source = new AbilitySourceInstance("Equipment", [new("endurance")]);
        Assert.ThrowsExactly<InvalidOperationException>(() => ChangeSources(context, owner, materialize: [source]));
        Assert.IsFalse(source.IsMaterialized);
        Assert.IsNull(original.Binding.Source);
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, owner.Property.LifePointMax);
        TestContext.WriteLine("A holderless materialization rejected a factory that returned an already mounted object.");
    }

    [TestMethod]
    public void SourceFactoryCannotShareOneGateBetweenSelectedAndUnselectedAbilities()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: pulse
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
                data:
                  gate:
                    charges: { maximum: 2, initial: 0, recovery: { intervalSeconds: 1, mode: Parallel } }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        CommandGateManager? shared = null;
        owner.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory = (id, level) =>
        {
            IReadOnlyList<IHandler> parts = catalog.Require(id).CreateHandlers(level);
            var ability = parts.OfType<ProjectileAbility>().Single();
            shared ??= ability.GateManager;
            ability.GateManager = shared;
            return parts;
        };
        var lower = new AbilitySourceInstance("Equipment", [new("pulse", 1)]);
        var higher = new AbilitySourceInstance("Equipment", [new("pulse", 2)]);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ChangeSources(context, owner, [new(lower, owner), new(higher, owner)]));
        Assert.IsFalse(lower.IsMaterialized);
        Assert.IsFalse(higher.IsMaterialized);
        Assert.IsEmpty(owner.Abilities.Sources);
        Assert.IsEmpty(owner.EventReceiver.GetHandlers<ProjectileAbility>());
        TestContext.WriteLine("Selected and reserve abilities were validated together; a shared mutable gate prevented the entire commit.");
    }

    [TestMethod]
    public void CommittedSourceObserverFailureStillStartsEveryTimerAndSchedulesCompletion()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: pulse
                type: Projectile
                allowMultiple: true
                trigger: { type: Command }
                data:
                  gate:
                    charges: { maximum: 2, initial: 0, recovery: { intervalSeconds: 1, mode: Parallel } }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var source = new AbilitySourceInstance("Equipment", [new("pulse"), new("pulse")]);
        var request = context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = owner, Assignments = [new(source, owner)], Receiver = owner.EventReceiver
        };
        bool failed = false;
        Action<IEvent> observer = ev =>
        {
            if (ev is AbilityChargesChangedEvent && !failed)
            {
                failed = true;
                throw new InvalidOperationException("committed charge publication failed");
            }
        };
        context.EventQueued += observer;
        context.Dispatch(request).ToList();
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        context.EventQueued -= observer;
        StringAssert.Contains(error.ToString(), "committed charge publication failed");
        Assert.AreEqual(request.OperationId, error.OperationId);
        Assert.AreSame(owner, source.Holder);
        Assert.HasCount(2, owner.EventReceiver.GetHandlers<ProjectileAbility>());
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        foreach (ProjectileAbility ability in source.Abilities.Cast<ProjectileAbility>())
            Assert.AreEqual(2, ability.GateManager!.GetChargeSnapshot()!.Available);
        Assert.HasCount(1, owner.EventReceiver.History.OfType<AbilitySourcesChangedEvent>());
        TestContext.WriteLine("A post-commit publication fault reported the committed operation, preserved both registrations, started both timers and did not lose completion.");
    }

    [TestMethod]
    public void SourceSwitchKeepsAcceptedCastAndUpgradeAndTransferKeepTheOriginalGate()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: delivery
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
              - id: recovery
                type: Heal
                allowMultiple: false
                trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                data:
                  amount: 7
                  gate:
                    windupSeconds: 1
                    cooldownSeconds: 4
                    occupiesWindup: false
                    charges: { maximum: 2, initial: 2 }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        firstOwner.Property.LifePoint = secondOwner.Property.LifePoint = 50;
        var lower = new AbilitySourceInstance("Equipment", [new("recovery", 1)]);
        var higher = new AbilitySourceInstance("Equipment", [new("recovery", 5)]);
        ChangeSources(context, firstOwner, [new(lower, firstOwner)]);
        var original = (HealAbility)lower.Abilities.Single();
        CommandGateManager gate = original.GateManager!;
        void Hit(Unit owner)
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "delivery",
                Source = owner, Target = owner, Receiver = owner.EventReceiver
            })).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }
        Hit(firstOwner);
        Assert.IsTrue(gate.HasPendingReservation);
        ChangeSources(context, firstOwner, [new(higher, firstOwner)]);
        var stronger = (HealAbility)higher.Abilities.Single();
        Assert.AreSame(stronger, firstOwner.EventReceiver.GetHandlers<HealAbility>().Single());
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(57, firstOwner.Property.LifePoint);
        HealEvent oldCast = firstOwner.EventReceiver.History.OfType<HealEvent>().Single();
        Assert.AreEqual(original.RuntimeInstanceId, oldCast.HealAbility.Ability!.RuntimeInstanceId);
        Assert.AreEqual(1, oldCast.HealAbility.Ability.Level);
        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        ChangeSources(context, firstOwner, levels: [new(lower, original.RuntimeInstanceId, 6)]);
        Assert.AreSame(original, firstOwner.EventReceiver.GetHandlers<HealAbility>().Single());
        Assert.AreSame(gate, original.GateManager);
        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        Hit(firstOwner);
        Assert.IsFalse(gate.HasPendingReservation);
        Assert.HasCount(1, firstOwner.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.CoolingDown));
        Assert.AreEqual(2, stronger.GateManager!.GetChargeSnapshot()!.Available);
        ChangeSources(context, firstOwner, [new(lower, secondOwner)]);
        Assert.AreSame(original, secondOwner.EventReceiver.GetHandlers<HealAbility>().Single());
        Hit(secondOwner);
        Assert.IsFalse(gate.HasPendingReservation);
        Assert.HasCount(1, secondOwner.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.CoolingDown));
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Hit(secondOwner);
        context.AdvanceTo(TimeSpan.FromSeconds(6));
        Assert.AreEqual(57, secondOwner.Property.LifePoint);
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        HealEvent newCast = secondOwner.EventReceiver.History.OfType<HealEvent>().Single();
        Assert.AreEqual(original.RuntimeInstanceId, newCast.HealAbility.Ability!.RuntimeInstanceId);
        Assert.AreEqual(6, newCast.HealAbility.Ability.Level);
        Assert.AreEqual(lower.InstanceId, newCast.HealAbility.Ability.GrantSourceInstanceId);
        Assert.AreEqual(1, oldCast.HealAbility.Ability.Level);
        Assert.AreEqual(2, stronger.GateManager.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("The accepted old cast survived selection change; an in-place upgrade and owner transfer retained the original gate, remaining charge and cooldown, without falling back to the reserve.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SourceAuthorityIsRecheckedAtReleaseAndNewOwnerDoesNotOverwriteOldCast(bool transfer)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: delivery
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
              - id: recovery
                type: Heal
                allowMultiple: false
                trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                data:
                  amount: 7
                  gate:
                    windupSeconds: 1
                    occupiesWindup: false
                    charges: { maximum: 1, initial: 1 }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        firstOwner.Property.LifePoint = secondOwner.Property.LifePoint = 50;
        var source = new AbilitySourceInstance("Equipment", [new("recovery")]);
        ChangeSources(context, firstOwner, [new(source, firstOwner)]);
        var ability = (HealAbility)source.Abilities.Single();
        void Hit(Unit owner)
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "delivery",
                Source = owner, Target = owner, Receiver = owner.EventReceiver
            })).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }
        Hit(firstOwner);
        Assert.IsTrue(ability.GateManager!.HasPendingReservation);
        ChangeSources(context, firstOwner, [new(source, transfer ? secondOwner : null)]);
        if (transfer) Hit(secondOwner);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(50, firstOwner.Property.LifePoint);
        Assert.IsEmpty(firstOwner.EventReceiver.History.OfType<HealEvent>());
        Assert.HasCount(1, firstOwner.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.AbilityNotAvailable));
        Assert.AreEqual(transfer ? 57 : 50, secondOwner.Property.LifePoint);
        Assert.AreEqual(transfer ? 0 : 1, ability.GateManager.GetChargeSnapshot()!.Available);
        Assert.IsFalse(ability.GateManager.HasPendingReservation);
        if (transfer)
        {
            Assert.AreSame(ability, secondOwner.EventReceiver.GetHandlers<HealAbility>().Single());
            Assert.AreEqual(ability.RuntimeInstanceId, secondOwner.EventReceiver.History
                .OfType<HealEvent>().Single().HealAbility.Ability!.RuntimeInstanceId);
        }
        TestContext.WriteLine($"Release rejected the old owner's revoked authority; transfer={transfer}, new-owner healing={secondOwner.Property.LifePoint - 50}, remaining charges={ability.GateManager.GetChargeSnapshot()!.Available}.");
    }

    private static AbilityCatalog LoadSourceCatalog(ServiceProvider provider, string yaml) =>
        provider.GetRequiredService<IContentCatalogLoader>().Load([Doc("sources.yaml", yaml)],
            [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");

    private static Unit CreateSourceUnit(
        Context context, AbilityCatalog catalog, string id, int maximumLevel = Unit.DefaultMaximumLevel)
    {
        Unit unit = CreateUnit(context, id, maximumLevel);
        context.AddEventReceiver(unit.EventReceiver);
        unit.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory =
            (abilityId, level) => catalog.Require(abilityId).CreateHandlers(level);
        return unit;
    }

    private static void ChangeSources(
        Context context, Unit coordinator, IReadOnlyList<AbilitySourceAssignment>? assignments = null,
        IReadOnlyList<AbilitySourceLevelChange>? levels = null, IReadOnlyList<AbilitySourceInstance>? materialize = null)
    {
        context.Dispatch(context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = coordinator, Assignments = assignments ?? [], LevelChanges = levels ?? [],
            Materialize = materialize ?? [], Receiver = coordinator.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
    }
}
