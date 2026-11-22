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
    [DataRow(null, 221, 223, 163)]
    [DataRow("[ProjectionChanged]", 221, 223, 163)]
    [DataRow("[]", 211, 211, 211)]
    [DataRow("[AbilityLevelChanged]", 221, 221, 221)]
    [DataRow("[TargetLevelChanged]", 211, 223, 223)]
    [DataRow("[AbilityLevelChanged, TargetLevelChanged]", 221, 223, 223)]
    public void PropertyRecalculationHonorsDeclaredLevelChangesInsteadOfEveryProjection(
        string? timing, int afterAbility, int afterUnit, int afterUnrelated)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        string declaration = timing == null ? "" : $"recalculateOn: {timing}";
        AbilityCatalog catalog = LoadSourceCatalog(provider, $$"""
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data:
                  {{declaration}}
                  modifiers: [{ target: lifePointMax, formula: "ability.level * 10 + target.level + target.life" }]
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var source = new AbilitySourceInstance("Equipment", [new("endurance")]);
        ChangeSources(context, owner, [new(source, owner)]);
        AbilityBase original = source.Abilities.Single();
        Assert.AreEqual(211, owner.Property.LifePointMax);
        ChangeSources(context, owner, levels: [new(source, original.RuntimeInstanceId, 2)]);
        Assert.AreEqual(afterAbility, owner.Property.LifePointMax);
        owner.Abilities.ReApply(levelOverride: 3);
        Assert.AreEqual(afterUnit, owner.Property.LifePointMax);
        owner.Property.LifePoint = 40;
        ChangeSources(context, owner, [new(new AbilitySourceInstance("Rule", []), owner)]);
        Assert.AreEqual(afterUnrelated, owner.Property.LifePointMax);
        Assert.AreEqual(40, owner.Property.LifePoint);
        ChangeSources(context, owner, [new(source, null)]);
        Assert.AreEqual(100, owner.Property.LifePointMax);
        ChangeSources(context, owner, [new(source, owner)]);
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(163, owner.Property.LifePointMax);
        TestContext.WriteLine($"recalculateOn={timing ?? "<omitted>"}: 211 -> {afterAbility} -> {afterUnit} -> {afterUnrelated}; a new registration reevaluated to 163 without recreating the ability.");
    }

    [TestMethod]
    public void PropertyRecalculationFailureDoesNotPublishNewLevelsOrCachedValues()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data:
                  recalculateOn: [AbilityLevelChanged]
                  modifiers:
                    - { target: lifePointMax, formula: "ability.level * 10" }
                    - { target: lifePointMax, formula: "10 / (3 - ability.level)" }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        var source = new AbilitySourceInstance("Equipment", [new("endurance")]);
        ChangeSources(context, owner, [new(source, owner)]);
        AbilityBase ability = source.Abilities.Single();
        Assert.AreEqual(115, owner.Property.LifePointMax);
        Assert.ThrowsExactly<ExpressionEvaluationException>(() =>
            ChangeSources(context, owner, levels: [new(source, ability.RuntimeInstanceId, 3)]));
        Assert.AreEqual(1, ability.Level);
        Assert.AreEqual(115, owner.Property.LifePointMax);
        owner.Abilities.ReApply();
        Assert.AreEqual(115, owner.Property.LifePointMax);
        ChangeSources(context, owner, levels: [new(source, ability.RuntimeInstanceId, 2)]);
        Assert.AreEqual(130, owner.Property.LifePointMax);
        Assert.AreSame(ability, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        TestContext.WriteLine("A later formula failure left the earlier modifier, runtime level and retained projection values unchanged; the next valid level change succeeded.");
    }

    [TestMethod]
    public void RetainedStatusModifiersMultiplyCurrentStacksWithoutResamplingUnselectedLevelChanges()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: fortify
                type: Status
                allowMultiple: false
                trigger: { type: None }
                data:
                  durationSeconds: 10
                  stackPolicy: ExtendAndStack
                  stackLimit: 2
                  recalculateOn: []
                  modifiers: [{ target: lifePointMax, formula: "target.level + 2" }]
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit target = CreateSourceUnit(context, catalog, "target");
        StatusApplicationOrigin origin = StatusApplicationOrigin.FromRule(target.ActionGate!, target.EventReceiver);
        void Apply()
        {
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = null, Target = target, AbilityIds = ["fortify"],
                Purpose = AbilityInstallPurpose.StatusApplication, ApplicationOrigin = origin,
                Receiver = target.EventReceiver
            }).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }
        Apply();
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreEqual(103, target.Property.LifePointMax);
        target.Abilities.ReApply(levelOverride: 5);
        Assert.AreEqual(103, target.Property.LifePointMax);
        Apply();
        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(2, original.CurrentStack);
        Assert.AreEqual(106, target.Property.LifePointMax);
        context.AdvanceTo(TimeSpan.FromSeconds(11));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("A retained +3 status value survived target level 1 -> 5, stacked to +6 rather than resampling to +14, and expired without leaving a modifier.");
    }

    [TestMethod]
    public void UnrepresentableRetainedStackRejectsWithoutChangingItsValueStackOrLease()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: fortify
                type: Status
                allowMultiple: false
                trigger: { type: None }
                data:
                  durationSeconds: 10
                  stackPolicy: ExtendAndStack
                  stackLimit: 3
                  recalculateOn: []
                  modifiers: [{ target: lifePointMax, formula: "1000000000 + target.level" }]
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit target = CreateSourceUnit(context, catalog, "target");
        StatusApplicationOrigin origin = StatusApplicationOrigin.FromRule(target.ActionGate!, target.EventReceiver);
        void Apply()
        {
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = null, Target = target, AbilityIds = ["fortify"],
                Purpose = AbilityInstallPurpose.StatusApplication, ApplicationOrigin = origin,
                Receiver = target.EventReceiver
            }).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }
        Apply();
        Apply();
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreEqual(2, original.CurrentStack);
        Assert.AreEqual(2000000102, target.Property.LifePointMax);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.ThrowsExactly<ExpressionEvaluationException>(Apply);
        Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
        Assert.AreEqual(2, original.CurrentStack);
        Assert.AreEqual(2000000102, target.Property.LifePointMax);
        context.AdvanceTo(TimeSpan.FromSeconds(11));
        Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("A third retained billion-point stack was rejected before commit; the two original stacks and original expiry remained intact.");
    }

    [TestMethod]
    public void RetainedPropertyValuesArePerRuntimeInstanceAndRefreshForANewHolder()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: true
                trigger: { type: None }
                data:
                  recalculateOn: []
                  modifiers: [{ target: lifePointMax, formula: "target.life" }]
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        secondOwner.Property.LifePoint = 40;
        var first = new AbilitySourceInstance("Equipment", [new("endurance")]);
        var second = new AbilitySourceInstance("Equipment", [new("endurance")]);
        ChangeSources(context, firstOwner, [new(first, firstOwner), new(second, secondOwner)]);
        AbilityBase transferred = second.Abilities.Single();
        Assert.AreEqual(200, firstOwner.Property.LifePointMax);
        Assert.AreEqual(140, secondOwner.Property.LifePointMax);
        firstOwner.Property.LifePoint = 75;
        secondOwner.Property.LifePoint = 20;
        ChangeSources(context, firstOwner, [new(second, firstOwner)]);
        Assert.AreEqual(275, firstOwner.Property.LifePointMax);
        Assert.AreEqual(100, secondOwner.Property.LifePointMax);
        Assert.AreSame(transferred, second.Abilities.Single());
        firstOwner.Property.LifePoint = 50;
        ChangeSources(context, firstOwner, materialize: [new AbilitySourceInstance("Rule", [])]);
        Assert.AreEqual(275, firstOwner.Property.LifePointMax);
        TestContext.WriteLine("Shared compiled modifiers retained separate +100/+40 instance values; transfer refreshed only the transferred original to +75, then both retained their own values.");
    }

    [TestMethod]
    [DataRow("[Unknown]")]
    [DataRow("[1]")]
    [DataRow("[None]")]
    [DataRow("[AbilityLevelChanged, AbilityLevelChanged]")]
    [DataRow("[ProjectionChanged, TargetLevelChanged]")]
    [DataRow("[null]")]
    public void InvalidPropertyRecalculationDeclarationsFailDuringLoading(string declaration)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        Assert.ThrowsExactly<InvalidDataException>(() => LoadSourceCatalog(provider, $$"""
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data:
                  recalculateOn: {{declaration}}
                  modifiers: [{ target: lifePointMax, formula: "10" }]
            """));
        TestContext.WriteLine($"Invalid recalculateOn declaration rejected: {declaration}");
    }
}
