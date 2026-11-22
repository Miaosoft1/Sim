using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Content.Talents.Model;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void WeightedTalentGrowthAwardsEachActualLevelAcrossJumpsWithoutHistoricalBackfill()
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        var catalog = LoadWeightedTalentContent(provider, "event.level.delta", "UnitLevelUp", initialPoints: 3);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        var abilities = catalog.GetSection<AbilityCatalog>("ability");
        TalentTreeDefinition definition = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("progression");
        Unit first = CreateSourceUnit(context, abilities, "first");
        Unit second = CreateSourceUnit(context, abilities, "second");
        second.SetLevel(4);
        UnitTalents firstTalents = AttachTalents(first, definition);
        UnitTalents secondTalents = AttachTalents(second, definition);
        var firstGrant = new AbilitySourceInstance("Rule", []);
        var secondGrant = new AbilitySourceInstance("Rule", []);
        AuthorizeTalents(context, first, definition, firstGrant, true);
        AuthorizeTalents(context, second, definition, secondGrant, true);
        TalentTreeInstance tree = firstTalents.Trees[definition.Id];
        TalentTreeInstance other = secondTalents.Trees[definition.Id];
        Assert.AreEqual(3, tree.AvailablePoints);
        Assert.AreEqual(3, other.AvailablePoints);
        Assert.IsTrue(RequestUnitLevelUp(context, first, 1, 4).Success);
        Assert.AreEqual(6, tree.AvailablePoints);
        Assert.IsFalse(RequestUnitLevelUp(context, first, 1, 5).Success);
        Assert.AreEqual(6, tree.AvailablePoints);
        Assert.IsTrue(RequestUnitLevelUp(context, first, 4, 10).Success);
        Assert.AreEqual(12, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(0, 9), tree.Growth["levels"]);
        Assert.IsTrue(RequestUnitLevelUp(context, second, 4, 5).Success);
        Assert.AreEqual(4, other.AvailablePoints);
        AuthorizeTalents(context, second, definition, secondGrant, false);
        RequestUnitLevelUp(context, second, 5, 8);
        AuthorizeTalents(context, second, definition, secondGrant, true);
        Assert.AreEqual(4, other.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(0, 1), other.Growth["levels"]);
        TestContext.WriteLine("Configured event.level.delta awarded 3+6 actual levels across 1->4->10, reaching 12 points including the initial three. Starting at level four did not backfill history; stale requests and upgrades while ungranted awarded nothing.");
    }

    [TestMethod]
    public void WeightedTalentGrowthDividesOneObservationIntoRewardsAndRemainderWithExplicitCaps()
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        var catalog = LoadWeightedTalentContent(provider, "11", progressPerReward: 5, maximumRewards: 1);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        TalentTreeDefinition definition = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("progression");
        UnitTalents talents = AttachTalents(owner, definition);
        AuthorizeTalents(context, owner, definition, new AbilitySourceInstance("Rule", []), true);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        SignalTalent(context, owner, "practice");
        Assert.AreEqual(1, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(1, 1), tree.Growth["levels"]);
        SignalTalent(context, owner, "practice");
        Assert.AreEqual(1, tree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(1, 1), tree.Growth["levels"]);
        Assert.AreEqual(TalentChangeFailure.GrowthLimitReached,
            owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Last().Failure);
        TestContext.WriteLine("One observation contributed eleven progress units: threshold five retained remainder one, while maximumRewards=1 limited reward occurrences rather than changing the configured progress or per-reward amount.");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("1.5")]
    [DataRow("2147483648")]
    [DataRow("1 / 0")]
    public void WeightedTalentGrowthRejectsInvalidQuantitiesWithoutChangingPointsOrCounter(string expression)
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        var catalog = LoadWeightedTalentContent(provider, expression);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        TalentTreeDefinition definition = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("progression");
        UnitTalents talents = AttachTalents(owner, definition);
        AuthorizeTalents(context, owner, definition, new AbilitySourceInstance("Rule", []), true);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        long revision = tree.Revision;
        Assert.ThrowsExactly<ExpressionEvaluationException>(() => SignalTalent(context, owner, "practice"));
        Assert.AreEqual(revision, tree.Revision);
        Assert.AreEqual(0, tree.AvailablePoints);
        Assert.IsEmpty(tree.Growth);
        Assert.IsFalse(owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Any(result =>
            result.Operation == TalentChangeOperation.ObserveGrowth && result.Success));
        TestContext.WriteLine($"Invalid progress '{expression}' failed explicitly; no rounding, silent default, point mutation or growth success was produced.");
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("event.level.delta")]
    public void WeightedTalentGrowthCompilesOnlyNumericFieldsOfItsDeclaredEventSchema(string expression)
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadWeightedTalentContent(provider, expression));
        StringAssert.Contains(error.Message, "weighted-talents.yaml");
        TestContext.WriteLine(error.Message);
    }

    [TestMethod]
    public void WeightedTalentGrowthRejectsPointOverflowBeforeConsumingAnyProgress()
    {
        using ServiceProvider provider = CreateTalentContentProvider();
        var catalog = LoadWeightedTalentContent(provider, "2147483647", initialPoints: 1);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        TalentTreeDefinition definition = catalog.GetSection<TalentTreeCatalog>("talentTree").Require("progression");
        UnitTalents talents = AttachTalents(owner, definition);
        AuthorizeTalents(context, owner, definition, new AbilitySourceInstance("Rule", []), true);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        long revision = tree.Revision;
        SignalTalent(context, owner, "practice");
        Assert.AreEqual(TalentChangeFailure.PointLimitExceeded,
            owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Last().Failure);
        Assert.AreEqual(revision, tree.Revision);
        Assert.AreEqual(1, tree.AvailablePoints);
        Assert.IsEmpty(tree.Growth);
        TestContext.WriteLine("A valid integral quantity exceeding point storage reported PointLimitExceeded without wrapping, consuming progress or claiming a reward.");
    }

    private static ContentCatalog LoadWeightedTalentContent(
        ServiceProvider provider, string progress, string trigger = "Signal",
        int initialPoints = 0, int progressPerReward = 1, long? maximumRewards = null)
    {
        string signal = trigger == "Signal" ? ", signalId: practice" : "";
        string maximum = maximumRewards.HasValue ? $", maximumRewards: {maximumRewards.Value}" : "";
        return provider.GetRequiredService<IContentCatalogLoader>().Load([Doc("weighted-talents.yaml", $$"""
            talentTree:
              - id: progression
                name: Progression
                initialPoints: {{initialPoints}}
                growthRules:
                  - { id: levels, abilityId: progression.growth, progressPerReward: {{progressPerReward}}{{maximum}} }
            ability:
              - id: progression.growth
                type: TalentGrowth
                allowMultiple: false
                trigger: { type: {{trigger}}{{signal}} }
                data: { treeId: progression, ruleId: levels, progress: "{{progress}}" }
            """)], TalentModules);
    }
}
