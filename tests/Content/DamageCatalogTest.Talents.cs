using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void TalentTreesKeepIndependentPointsAndOriginalNodeAbilitiesAcrossRealGrants()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "learner");
        var definition = new TalentTreeDefinition("endurance-tree", "Endurance", 3,
            [new("main", "Main", [new("endurance-node", "Endurance", 3, [new("endurance")], growAbilitiesWithRank: true)])]);
        var otherDefinition = new TalentTreeDefinition("other-tree", "Other", 2, []);
        UnitTalents talents = AttachTalents(owner, definition, otherDefinition);
        var role = new AbilitySourceInstance("Rule", []);
        var item = new AbilitySourceInstance("Equipment", []);
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, role, true).Success);
        Assert.IsTrue(AuthorizeTalents(context, owner, otherDefinition, role, true).Success);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        TalentTreeInstance other = talents.Trees[otherDefinition.Id];
        TalentTreeHandler observer = owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single(handler => handler.Tree == tree);
        Assert.IsTrue(AllocateTalent(context, tree, "endurance-node").Success);
        AbilityBase original = tree.NodeSources["endurance-node"].Abilities.Single();
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, item, true).Success);
        Assert.AreEqual(TalentChangeFailure.AlreadyGranted, AuthorizeTalents(context, owner, definition, item, true).Failure);
        Assert.AreEqual(2, tree.GrantCount);
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, role, false).Success);
        Assert.AreSame(observer, owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single(handler => handler.Tree == tree));
        long activeVersion = tree.AuthorityVersion;
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, item, false).Success);
        Assert.IsFalse(tree.IsAuthorized);
        Assert.AreEqual(100, owner.Property.LifePointMax);
        Assert.IsFalse(owner.EventReceiver.GetHandlers<TalentTreeHandler>().Any(handler => handler.Tree == tree));
        Assert.AreEqual(TalentChangeFailure.Inactive, AllocateTalent(context, tree, "endurance-node").Failure);
        Assert.IsTrue(other.IsAuthorized);
        Assert.AreEqual(2, other.AvailablePoints);
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, item, true).Success);
        Assert.AreSame(tree, talents.Trees[definition.Id]);
        Assert.AreSame(observer, owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single(handler => handler.Tree == tree));
        Assert.AreSame(original, tree.NodeSources["endurance-node"].Abilities.Single());
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.AreEqual(TalentChangeFailure.StaleAuthority,
            AllocateTalent(context, tree, "endurance-node", activeVersion).Failure);
        Assert.IsTrue(AllocateTalent(context, tree, "endurance-node").Success);
        Assert.AreSame(original, tree.NodeSources["endurance-node"].Abilities.Single());
        Assert.AreEqual(2, original.Level);
        Assert.AreEqual(120, owner.Property.LifePointMax);
        Assert.AreEqual(1, tree.AvailablePoints);
        Assert.AreEqual(2, other.AvailablePoints);
        TestContext.WriteLine("Distinct real grants shared one role-owned tree. Removing the last grant removed its own observer and effects; reauthorization restored the same tree, observer and ability, rejected an old epoch and upgraded the original ability in place.");
    }

    [TestMethod]
    public void TalentAllocationHonorsDependenciesPointThresholdsAndBidirectionalExclusions()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, LoadSourceCatalog(provider, InventoryAbilities), "learner");
        var definition = new TalentTreeDefinition("choices", "Choices", 5,
            [new("main", "Main",
            [
                new("foundation", "Foundation", 2, []),
                new("advanced", "Advanced", 1, [], tier: 2,
                    requirement: new("foundation", 2, 2, 2, ["alternative"])),
                new("alternative", "Alternative", 1, [])
            ])]);
        UnitTalents talents = AttachTalents(owner, definition);
        Assert.IsTrue(AuthorizeTalents(context, owner, definition, new AbilitySourceInstance("Rule", []), true).Success);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        Assert.AreEqual(TalentChangeFailure.RequirementsNotMet, AllocateTalent(context, tree, "advanced").Failure);
        Assert.IsTrue(AllocateTalent(context, tree, "foundation").Success);
        Assert.AreEqual(TalentChangeFailure.RequirementsNotMet, AllocateTalent(context, tree, "advanced").Failure);
        Assert.IsTrue(AllocateTalent(context, tree, "foundation").Success);
        Assert.IsTrue(AllocateTalent(context, tree, "advanced").Success);
        Assert.AreEqual(TalentChangeFailure.MaximumRank, AllocateTalent(context, tree, "advanced").Failure);
        Assert.AreEqual(TalentChangeFailure.RequirementsNotMet, AllocateTalent(context, tree, "alternative").Failure);
        Assert.AreEqual(2, tree.AvailablePoints);
        Assert.AreEqual(3, tree.Ranks.Values.Sum());
        TestContext.WriteLine("Prerequisites and explicit branch/tree point thresholds gated allocation. A one-sided exclusion blocked the reverse purchase too, and rejected purchases consumed no points.");
    }

    [TestMethod]
    public void TalentProjectionFailureRollsBackRankPointsAbilityLevelAndNotifications()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, LoadSourceCatalog(provider, InventoryAbilities), "learner");
        var definition = new TalentTreeDefinition("fragile-tree", "Fragile", 3,
            [new("main", "Main", [new("fragile-node", "Fragile", 2, [new("fragile")], growAbilitiesWithRank: true)])]);
        UnitTalents talents = AttachTalents(owner, definition);
        var grant = new AbilitySourceInstance("Rule", []);
        AuthorizeTalents(context, owner, definition, grant, true);
        TalentTreeInstance tree = talents.Trees[definition.Id];
        Assert.IsTrue(AllocateTalent(context, tree, "fragile-node").Success);
        AbilityBase original = tree.NodeSources["fragile-node"].Abilities.Single();
        long revision = tree.Revision;
        long sourceRevision = tree.NodeSources["fragile-node"].Revision;
        int successes = owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Count(result => result.Success);
        Assert.ThrowsExactly<Miaosoft.Sim.Combat.Skill.Expression.ExpressionEvaluationException>(() =>
            AllocateTalent(context, tree, "fragile-node"));
        Assert.AreEqual(revision, tree.Revision);
        Assert.AreEqual(sourceRevision, tree.NodeSources["fragile-node"].Revision);
        Assert.AreEqual(2, tree.AvailablePoints);
        Assert.AreEqual(1, tree.Ranks["fragile-node"]);
        Assert.AreEqual(1, original.Level);
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.AreEqual(successes, owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Count(result => result.Success));
        TestContext.WriteLine("A formula failure on rank two rolled back original ability level, rank, points, source/tree revisions and success publication together.");
    }

    [TestMethod]
    public void TalentProgressRechecksAuthorizationAfterQueuedRequestAndOwnerCannotChange()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        var catalog = LoadSourceCatalog(provider, InventoryAbilities);
        Unit first = CreateSourceUnit(context, catalog, "first");
        Unit second = CreateSourceUnit(context, catalog, "second");
        var definition = new TalentTreeDefinition("shared-definition", "Shared", 2,
            [new("main", "Main", [new("node", "Node", 2, [new("endurance")])])]);
        UnitTalents firstTalents = AttachTalents(first, definition);
        UnitTalents secondTalents = AttachTalents(second, definition);
        var grant = new AbilitySourceInstance("Equipment", []);
        AuthorizeTalents(context, first, definition, grant, true);
        TalentTreeInstance firstTree = firstTalents.Trees[definition.Id];
        long version = firstTree.AuthorityVersion;
        var allocate = context.Factory.NewEvent<TalentNodeAllocateRequestEvent>() with
        {
            Tree = firstTree, NodeId = "node", ExpectedAuthorityVersion = version,
            Receiver = first.EventReceiver, Priority = 10
        };
        context.Dispatch(allocate).ToList();
        AuthorizeTalents(context, first, definition, grant, false);
        Assert.AreEqual(TalentChangeFailure.Inactive,
            first.EventReceiver.History.OfType<TalentChangeResultEvent>().Single(result => result.OperationId == allocate.OperationId).Failure);
        AuthorizeTalents(context, second, definition, grant, true);
        TalentTreeInstance secondTree = secondTalents.Trees[definition.Id];
        Assert.AreNotSame(firstTree, secondTree);
        Assert.AreEqual(2, secondTree.AvailablePoints);
        Assert.IsEmpty(secondTree.Ranks);
        Assert.IsTrue(AllocateTalent(context, secondTree, "node").Success);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ChangeSources(context, second, [new(secondTree.NodeSources["node"], first)]));
        Assert.AreSame(second, secondTree.NodeSources["node"].Holder);
        Assert.IsEmpty(firstTree.Ranks);
        Assert.AreEqual(100, first.Property.LifePointMax);
        Assert.AreEqual(110, second.Property.LifePointMax);
        TestContext.WriteLine("A queued allocation lost authorization before actual preparation and changed nothing. Moving the grant to another unit used that unit's own tree and point pool, never the first unit's progress.");
    }

    private static UnitTalents AttachTalents(Unit owner, params TalentTreeDefinition[] definitions)
    {
        var talents = new UnitTalents(owner, definitions);
        owner.EventReceiver.AddHandler(new TalentDirectoryHandler(talents));
        return talents;
    }

    private static TalentChangeResultEvent AuthorizeTalents(
        Context context, Unit owner, TalentTreeDefinition definition, IAbilitySource grant, bool authorize)
    {
        var request = context.Factory.NewEvent<TalentAuthorizationRequestEvent>() with
        {
            Owner = owner, Definition = definition, Grant = grant, Authorize = authorize, Receiver = owner.EventReceiver
        };
        context.Dispatch(request).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Single(result => result.OperationId == request.OperationId);
    }

    private static TalentChangeResultEvent AllocateTalent(
        Context context, TalentTreeInstance tree, string nodeId, long? authorityVersion = null)
    {
        var request = context.Factory.NewEvent<TalentNodeAllocateRequestEvent>() with
        {
            Tree = tree, NodeId = nodeId, ExpectedAuthorityVersion = authorityVersion ?? tree.AuthorityVersion,
            Receiver = tree.Owner.EventReceiver
        };
        context.Dispatch(request).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return tree.Owner.EventReceiver.History.OfType<TalentChangeResultEvent>().Single(result => result.OperationId == request.OperationId);
    }
}
