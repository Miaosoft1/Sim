using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void ActualUnitLevelIncreasePublishesOnceWithinItsConfiguredRangeWithoutUpgradingItsAbility()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadLevelingCatalog(provider, "target.level * 10");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner", maximumLevel: 12);
        var source = new AbilitySourceInstance("Character", [new("endurance", 2)]);
        ChangeSources(context, owner, [new(source, owner)]);
        AbilityBase original = source.Abilities.Single();
        Assert.AreEqual(110, owner.Property.LifePointMax);
        UnitLevelUpResultEvent result = RequestUnitLevelUp(context, owner, 1, 11, priority: -40);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(result.OperationId, result.CommittedOperationId);
        Assert.AreEqual(11, owner.Property.Level);
        Assert.AreEqual(210, owner.Property.LifePointMax);
        Assert.AreEqual(2, original.Level);
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        UnitLevelUpEvent increased = owner.EventReceiver.History.OfType<UnitLevelUpEvent>().Single();
        Assert.AreEqual(1, increased.OldLevel);
        Assert.AreEqual(11, increased.NewLevel);
        Assert.AreEqual(result.OperationId, increased.OperationId);
        Assert.AreEqual(-40, increased.Priority);
        Assert.AreEqual(UnitLevelUpFailure.StaleLevel, RequestUnitLevelUp(context, owner, 1, 11).Failure);
        Assert.AreEqual(UnitLevelUpFailure.NotAnIncrease, RequestUnitLevelUp(context, owner, 11, 11).Failure);
        Assert.AreEqual(UnitLevelUpFailure.OutsideLevelRange, RequestUnitLevelUp(context, owner, 11, 13).Failure);
        Assert.HasCount(1, owner.EventReceiver.History.OfType<UnitLevelUpEvent>());
        Assert.IsTrue(RequestUnitLevelUp(context, owner, 11, 12).Success);
        Assert.AreEqual(220, owner.Property.LifePointMax);
        Assert.HasCount(2, owner.EventReceiver.History.OfType<UnitLevelUpEvent>());
        TestContext.WriteLine("A configured level-12 unit upgraded 1->11->12, publishing only actual increases with original priority; stale/no-change/out-of-range requests granted nothing and its real level-2 ability did not change.");
    }

    [TestMethod]
    public void UnitLevelCompletionReturnsToItsRegisteredCoordinatorWithoutDuplicatingTheUpgrade()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadLevelingCatalog(provider, "target.level * 10");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        using var foreign = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = foreign.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        Unit coordinator = CreateSourceUnit(context, catalog, "coordinator");
        Unit outsider = CreateSourceUnit(foreign, catalog, "outsider");
        ChangeSources(context, owner, [new(new AbilitySourceInstance("Character", [new("endurance")]), owner)]);
        UnitLevelUpResultEvent Request(int expectedLevel)
        {
            var request = context.Factory.NewEvent<UnitLevelUpRequestEvent>() with
            {
                Unit = owner, ExpectedLevel = expectedLevel, NewLevel = 2,
                Receiver = owner.EventReceiver, CompletionReceiver = coordinator.EventReceiver, Priority = -40
            };
            context.Dispatch(request).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
            return coordinator.EventReceiver.History.OfType<UnitLevelUpResultEvent>()
                .Single(result => result.OperationId == request.OperationId);
        }

        UnitLevelUpResultEvent success = Request(1);
        Assert.IsTrue(success.Success);
        Assert.AreEqual(success.OperationId, success.CommittedOperationId);
        Assert.AreEqual(-40, success.Priority);
        Assert.AreEqual(2, owner.Property.Level);
        Assert.AreEqual(120, owner.Property.LifePointMax);
        Assert.AreEqual(UnitLevelUpFailure.StaleLevel, Request(1).Failure);
        Assert.IsEmpty(owner.EventReceiver.History.OfType<UnitLevelUpResultEvent>());
        Assert.HasCount(1, owner.EventReceiver.History.OfType<UnitLevelUpEvent>());
        Assert.IsEmpty(coordinator.EventReceiver.History.OfType<UnitLevelUpEvent>());
        foreach (EventReceiver invalid in new[] { outsider.EventReceiver, context.Factory.NewEventReceiver() })
        {
            context.Dispatch(context.Factory.NewEvent<UnitLevelUpRequestEvent>() with
            {
                Unit = owner, ExpectedLevel = 2, NewLevel = 3,
                Receiver = owner.EventReceiver, CompletionReceiver = invalid
            }).ToList();
            StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(
                () => context.AdvanceTo(context.ContextTimeSpan)).Message, "completion receiver");
            Assert.AreEqual(2, owner.Property.Level);
        }
        Assert.HasCount(2, coordinator.EventReceiver.History.OfType<UnitLevelUpResultEvent>());
        Assert.HasCount(1, owner.EventReceiver.History.OfType<UnitLevelUpEvent>());
        TestContext.WriteLine("Only success/failure results returned to the registered coordinator; one committed upgrade remained on the actual unit. Foreign and unregistered completion destinations failed before any level or projection change.");
    }

    [TestMethod]
    public void UnitLevelProjectionFailureRollsBackLevelAndCacheBeforeAnyUpgradePublication()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadLevelingCatalog(provider, "10 / (2 - target.level)");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var source = new AbilitySourceInstance("Character", [new("endurance")]);
        ChangeSources(context, owner, [new(source, owner)]);
        AbilityBase original = source.Abilities.Single();
        owner.Property.Experience = 40;
        Assert.ThrowsExactly<ExpressionEvaluationException>(() => RequestUnitLevelUp(context, owner, 1, 2));
        Assert.AreEqual(1, owner.Property.Level);
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.AreEqual(40, owner.Property.Experience);
        Assert.IsEmpty(owner.EventReceiver.History.OfType<UnitLevelUpEvent>());
        Assert.IsEmpty(owner.EventReceiver.History.OfType<UnitLevelUpResultEvent>());
        Assert.IsTrue(RequestUnitLevelUp(context, owner, 1, 3).Success);
        Assert.AreEqual(90, owner.Property.LifePointMax);
        Assert.AreEqual(40, owner.Property.Experience);
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(1, owner.EventReceiver.History.OfType<UnitLevelUpEvent>().Single().OldLevel);
        TestContext.WriteLine("A level-2 formula failure preserved level 1, life projection, experience and caches with no success; a subsequent valid level-3 transaction used the same original ability.");
    }

    [TestMethod]
    public void CommittedUnitLevelPublicationFailureStillDeliversItsResultAndCannotGrantTheSameIncreaseTwice()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadLevelingCatalog(provider, "target.level * 10");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        ChangeSources(context, owner, [new(new AbilitySourceInstance("Character", [new("endurance")]), owner)]);
        Guid operationId = Guid.NewGuid();
        Action<IEvent> observer = ev =>
        {
            if (ev is UnitLevelUpEvent)
                throw new InvalidOperationException("upgrade publication failed after commit");
        };
        context.EventQueued += observer;
        context.Dispatch(context.Factory.NewEvent<UnitLevelUpRequestEvent>() with
        {
            Unit = owner, ExpectedLevel = 1, NewLevel = 2,
            OperationId = operationId, Receiver = owner.EventReceiver
        }).ToList();
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        context.EventQueued -= observer;
        Assert.AreEqual(operationId, error.OperationId);
        Assert.AreEqual(2, owner.Property.Level);
        Assert.AreEqual(120, owner.Property.LifePointMax);
        context.AdvanceTo(TimeSpan.Zero);
        UnitLevelUpResultEvent result = owner.EventReceiver.History.OfType<UnitLevelUpResultEvent>().Single();
        Assert.IsTrue(result.Success);
        Assert.AreEqual(operationId, result.CommittedOperationId);
        Assert.AreEqual(UnitLevelUpFailure.StaleLevel, RequestUnitLevelUp(context, owner, 1, 2).Failure);
        Assert.HasCount(1, owner.EventReceiver.History.OfType<UnitLevelUpEvent>());
        TestContext.WriteLine("An upgrade publication fault remained explicitly committed, delivered its result and real upgrade event, and rejected the old expected level instead of awarding another increase.");
    }

    [TestMethod]
    public void CommittedUnitUpgradeRoutesOnceToSelfAndAllyRulesUsingItsTypedActualLevelDelta()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: self-reward
                type: Status
                allowMultiple: false
                trigger:
                  type: UnitLevelUp
                  conditions:
                    - { type: Expression, expression: "event.level.old == 2" }
                    - { type: Expression, expression: "event.level.new == 4" }
                    - { type: Expression, expression: "event.level.delta == 2" }
                data:
                  durationSeconds: 5
                  modifiers: [{ target: lifePointMax, formula: "3" }]
              - id: ally-reward
                type: Status
                allowMultiple: false
                trigger:
                  type: UnitLevelUp
                  eventSourceScope: Ally
                  conditions: [{ type: Expression, expression: "event.level.delta == 2" }]
                data:
                  durationSeconds: 5
                  modifiers: [{ target: lifePointMax, formula: "7" }]
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit first = CreateSourceUnit(context, catalog, "first");
        Unit second = CreateSourceUnit(context, catalog, "second");
        first.TeamId = second.TeamId = 7;
        ChangeSources(context, first, [new(new AbilitySourceInstance("Talent", [new("self-reward")]), first)]);
        ChangeSources(context, second, [new(new AbilitySourceInstance("Talent", [new("ally-reward")]), second)]);
        Assert.IsTrue(RequestUnitLevelUp(context, first, 1, 2).Success);
        Assert.AreEqual(100, first.Property.LifePointMax);
        UnitLevelUpResultEvent result = RequestUnitLevelUp(context, first, 2, 4, priority: -40);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(110, first.Property.LifePointMax);
        Assert.AreEqual(100, second.Property.LifePointMax);
        AbilityTriggerInvokeEvent[] invocations = first.EventReceiver.History.OfType<AbilityTriggerInvokeEvent>()
            .Concat(second.EventReceiver.History.OfType<AbilityTriggerInvokeEvent>())
            .Where(ev => ev.UpstreamEvent is UnitLevelUpEvent).ToArray();
        Assert.HasCount(2, invocations);
        UnitLevelUpEvent upgrade = first.EventReceiver.History.OfType<UnitLevelUpEvent>()
            .Single(ev => ev.OperationId == result.OperationId);
        Assert.IsTrue(invocations.All(ev => ReferenceEquals(upgrade, ev.UpstreamEvent) && ev.Priority == -40));
        context.Dispatch(upgrade with { Receiver = first.EventReceiver }).ToList();
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(result.OperationId, error.OperationId);
        Assert.AreEqual(110, first.Property.LifePointMax);
        TestContext.WriteLine("Only the committed 2->4 delta matched typed rules; one publication activated self and ally once with original priority and real upgraded target, and a cloned republication was rejected.");
    }

    [TestMethod]
    public void UpgradeSnapshotFieldsAreRejectedForUnrelatedTriggerTypes()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadSourceCatalog(provider, """
                ability:
                  - id: invalid
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: Heal
                      conditions: [{ type: Expression, expression: "event.level.old > 0" }]
                """));
        StringAssert.Contains(error.Message, "event.level.old");
        TestContext.WriteLine(error.Message);
    }

    private static AbilityCatalog LoadLevelingCatalog(ServiceProvider provider, string formula) =>
        LoadSourceCatalog(provider, $$"""
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data:
                  recalculateOn: [TargetLevelChanged]
                  modifiers: [{ target: lifePointMax, formula: "{{formula}}" }]
            """);

    private static UnitLevelUpResultEvent RequestUnitLevelUp(
        Context context, Unit owner, int expectedLevel, int newLevel, int priority = 0)
    {
        Guid operationId = Guid.NewGuid();
        context.Dispatch(context.Factory.NewEvent<UnitLevelUpRequestEvent>() with
        {
            Unit = owner, ExpectedLevel = expectedLevel, NewLevel = newLevel, OperationId = operationId,
            Receiver = owner.EventReceiver, Priority = priority
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return owner.EventReceiver.History.OfType<UnitLevelUpResultEvent>()
            .Single(result => result.OperationId == operationId);
    }
}
