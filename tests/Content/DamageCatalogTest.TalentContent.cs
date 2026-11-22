using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Content.Registration;
using Miaosoft.Sim.Loadout.Content.Talents.Model;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    private const string TalentGrowthContent = """
        talentTree:
          - id: practice
            name: Practice
            initialPoints: 0
            branches:
              - id: main
                name: Main
                nodes:
                  - id: endurance-node
                    name: Endurance
                    maximumRank: 3
                    growAbilitiesWithRank: true
                    abilities: [{ id: endurance }]
            growthRules:
              - { id: repetitions, abilityId: practice.growth, progressPerReward: 5 }
              - { id: once, abilityId: practice.once, pointsPerReward: 3, maximumRewards: 1 }
              - { id: upgrade, abilityId: practice.upgrade, pointsPerReward: 2 }
        ability:
          - id: practice.growth
            type: TalentGrowth
            allowMultiple: false
            trigger: { type: Signal, signalId: practice }
            data: { treeId: practice, ruleId: repetitions }
          - id: practice.once
            type: TalentGrowth
            allowMultiple: false
            trigger: { type: Signal, signalId: first }
            data: { treeId: practice, ruleId: once }
          - id: practice.upgrade
            type: TalentGrowth
            allowMultiple: false
            trigger:
              type: UnitLevelUp
              conditions: [{ type: Expression, expression: "event.level.delta == 2" }]
            data: { treeId: practice, ruleId: upgrade }
        """;

    [TestMethod]
    public void TalentGrowthContentCountsThresholdsRetainsRemainderAndDoesNotCatchUpWhileUnauthorized()
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        ContentCatalog catalog = LoadTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        TalentTreeDefinition definition = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("practice");
        var grant = new AbilitySourceInstance("Rule", []);
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, grant, true).Success);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        AbilityBase original = tree.GrowthSource.Abilities.Single(ability => ability.Id == "practice.growth");
        for (int index = 0; index < 11; index++) SignalTalent(context, owner, "practice");
        Assert.AreEqual(2, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(1, 2), tree.Growth["repetitions"]);
        TalentGrowthObservationEvent observed = owner.EventReceiver.History.OfType<TalentGrowthObservationEvent>().Last();
        context.Dispatch(observed with { }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        Assert.AreEqual(2, tree.AvailablePoints);
        Assert.AreEqual(TalentChangeFailure.AlreadyObserved,
            owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Last().Failure);
        AuthorizeTalents(context, owner, definition, grant, false);
        int observations = owner.EventReceiver.History.OfType<TalentGrowthObservationEvent>().Count();
        for (int index = 0; index < 9; index++) SignalTalent(context, owner, "practice");
        Assert.HasCount(observations, owner.EventReceiver.History.OfType<TalentGrowthObservationEvent>());
        Assert.AreEqual(new TalentGrowthProgress(1, 2), tree.Growth["repetitions"]);
        AuthorizeTalents(context, owner, definition, grant, true);
        Assert.AreSame(original, tree.GrowthSource.Abilities.Single(ability => ability.Id == "practice.growth"));
        for (int index = 0; index < 4; index++) SignalTalent(context, owner, "practice");
        Assert.AreEqual(3, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(0, 3), tree.Growth["repetitions"]);
        SignalTalent(context, owner, "first");
        SignalTalent(context, owner, "first");
        Assert.AreEqual(6, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(0, 1), tree.Growth["once"]);
        Assert.IsEmpty(owner.EventReceiver.GetHandlers<InventoryHandler>());
        TestContext.WriteLine("The selected talent module compiled forward ability references. Eleven valid signals yielded two points and one remainder; cloned observation was not counted twice. Nine ungranted signals were ignored, original growth abilities resumed with the remainder, and a one-time reward ran once. Inventory was never enabled.");
    }

    [TestMethod]
    public void TalentGrowthUsesCommittedTypedUpgradeRulesAndRejectsAnOldQueuedInvocationAfterRegrant()
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        ContentCatalog catalog = LoadTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        TalentTreeDefinition definition = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("practice");
        var grant = new AbilitySourceInstance("Rule", []);
        AuthorizeTalents(context, owner, definition, grant, true);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        RequestUnitLevelUp(context, owner, 1, 2);
        Assert.AreEqual(0, tree.AvailablePoints);
        RequestUnitLevelUp(context, owner, 2, 4, priority: -40);
        Assert.AreEqual(2, tree.AvailablePoints);
        TalentGrowthObservationEvent observed = owner.EventReceiver.History.OfType<TalentGrowthObservationEvent>().Single();
        Assert.AreEqual(-40, observed.Priority);
        Assert.IsInstanceOfType<UnitLevelUpEvent>(observed.UpstreamEvent);
        SignalTalent(context, owner, "practice");
        AbilityTriggerInvokeEvent invocation = owner.EventReceiver.History.OfType<AbilityTriggerInvokeEvent>()
            .Last(ev => ev.AbilityId == "practice.growth");
        context.Dispatch(invocation with { Priority = 100 }).ToList();
        context.Dispatch(context.Factory.NewEvent<TalentAuthorizationRequestEvent>() with
        {
            Owner = owner, Definition = definition, Grant = grant, Authorize = false,
            Receiver = owner.EventReceiver, Priority = -20
        }).ToList();
        context.Dispatch(context.Factory.NewEvent<TalentAuthorizationRequestEvent>() with
        {
            Owner = owner, Definition = definition, Grant = grant, Authorize = true,
            Receiver = owner.EventReceiver, Priority = -10
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        Assert.AreEqual(2, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(1, 0), tree.Growth["repetitions"]);
        Assert.AreEqual(TalentChangeFailure.StaleAuthority,
            owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Last().Failure);
        TestContext.WriteLine("Only the committed +2 upgrade matched the typed rule, preserving priority -40. An old instance invocation queued before revoke/regrant failed its captured source-authority epoch and did not add progress.");
    }

    [TestMethod]
    public void TalentOnlyNeutralContentNeedsNoAbilityOrInventoryModule()
    {
        using ServiceProvider provider = new ServiceCollection().AddContentLoading().AddTalentContent().BuildServiceProvider();
        ContentCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load(
            [Doc("neutral-talents.yaml", "talentTree: [{ id: neutral, name: Neutral, initialPoints: 1 }]")],
            [LoadoutContentServiceCollectionExtensions.TalentModuleId]);
        using var context = new Context();
        context.Logger.Output = null;
        Unit owner = CreateUnit(context, "learner");
        context.AddEventReceiver(owner.EventReceiver);
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        Assert.IsTrue(AuthorizeTalents(context, owner,
            catalog.GetSection<TalentTreeCatalog>("talentTree").Require("neutral"), new AbilitySourceInstance("Rule", []), true).Success);
        Assert.AreEqual(1, talents.Trees["neutral"].AvailablePoints);
        Assert.HasCount(1, owner.EventReceiver.GetHandlers<TalentTreeHandler>());
        TestContext.WriteLine("A neutral tree could load and authorize without ability content or any inventory/trade feature.");
    }

    [TestMethod]
    public void TalentTreeFragmentsMergeBranchesAndFillHeaderData()
    {
        using ServiceProvider provider = new ServiceCollection().AddContentLoading().AddTalentContent().BuildServiceProvider();
        ContentCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load(
        [
            Doc("tree-header.yaml", "talentTree: [{ id: shared, name: Shared, initialPoints: 2 }]"),
            Doc("tree-branch.yaml", """
                talentTree:
                  - id: shared
                    branches:
                      - id: second
                        name: Second
                        nodes: []
                """),
            Doc("tree-first-branch.yaml", """
                talentTree:
                  - id: shared
                    branches:
                      - id: first
                        name: First
                        nodes: []
                """)
        ], [LoadoutContentServiceCollectionExtensions.TalentModuleId]);
        var tree = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("shared");
        Assert.AreEqual("Shared", tree.Name);
        Assert.AreEqual(2, tree.InitialPoints);
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, tree.Branches.Select(branch => branch.Id).ToArray());
    }

    [TestMethod]
    [DataRow("talentTree: [{ id: invalid, name: Invalid, branches: null }]")]
    [DataRow("talentTree: [{ id: invalid, name: Invalid, branches: [{ id: main, name: Main, nodes: [{ id: node, name: Node, requirement: { dependsOnNodeId: missing } }] }] }]")]
    [DataRow("talentTree: [{ id: invalid, name: Invalid, branches: [{ id: main, name: Main, nodes: [{ id: one, name: One, requirement: { dependsOnNodeId: two } }, { id: two, name: Two, requirement: { dependsOnNodeId: one } }] }] }]")]
    [DataRow("talentTree: [{ id: invalid, name: Invalid, privateTheme: ignored }]")]
    [DataRow("talentTree: [{ id: invalid, name: Invalid, growthRules: [{ id: growth, abilityId: endurance }] }]")]
    [DataRow("talentTree: [{ id: invalid, name: Invalid, growthRules: [{ id: growth, abilityId: missing }] }]")]
    public void TalentContentRejectsInvalidGraphOrGrowthBindingsWithTheirSource(string yaml)
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("bad-talents.yaml", yaml), Doc("abilities.yaml", InventoryAbilities)], TalentModules));
        StringAssert.Contains(error.Message, "bad-talents.yaml");
        TestContext.WriteLine(error.Message);
    }

    [TestMethod]
    public void RegisteredButUnselectedTalentsRejectTheirRootsAndGrowthAbilityType()
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("unselected.yaml", "talentTree: [{ id: neutral, name: Neutral }]")], []));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("unselected-growth.yaml", """
                    ability:
                      - id: growth
                        type: TalentGrowth
                        allowMultiple: false
                        trigger: { type: Signal, signalId: practice }
                        data: { treeId: missing, ruleId: missing }
                    """)], [SkillContentServiceCollectionExtensions.AbilityModuleId,
                    SkillContentServiceCollectionExtensions.DamageModuleId]));
        TestContext.WriteLine("Unselected talent content expanded neither root sections nor the ability parser set.");
    }

    private static readonly string[] TalentModules =
    [
        LoadoutContentServiceCollectionExtensions.TalentModuleId,
        SkillContentServiceCollectionExtensions.AbilityModuleId,
        SkillContentServiceCollectionExtensions.DamageModuleId
    ];

    private static ServiceProvider CreateTalentContentProvider() =>
        new ServiceCollection().AddContentLoading().AddAbilityContent().AddDamageTypeContent()
            .AddTalentContent().BuildServiceProvider();

    private static ContentCatalog LoadTalentContent(ServiceProvider provider) =>
        provider.GetRequiredService<IContentCatalogLoader>().Load(
            [Doc("talents.yaml", TalentGrowthContent), Doc("abilities.yaml", InventoryAbilities)], TalentModules);

    private static void SignalTalent(Context context, Unit owner, string signalId)
    {
        context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            SignalId = signalId, CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
            Receiver = owner.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
    }
}
