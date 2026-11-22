using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Content.Inventory.Model;
using Miaosoft.Sim.Loadout.Content.Registration;
using Miaosoft.Sim.Loadout.Content.Talents.Model;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    private const string EquipmentTalentContent = """
        equipmentTalent:
          - { itemId: first-tool, treeIds: [practice] }
          - { itemId: second-tool, treeIds: [practice] }
          - { itemId: fragile-tool, treeIds: [practice] }
        item:
          - { id: first-tool, name: First, slotType: weapon }
          - { id: second-tool, name: Second, slotType: weapon }
          - id: fragile-tool
            name: Fragile
            slotType: weapon
            abilities: [{ id: fragile, level: 2 }]
        """;

    [TestMethod]
    public void EquipmentTalentContentGrantsOnlyWhenEquippedAndReplacementKeepsTheSameAuthorizedTree()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        ContentCatalog catalog = LoadEquipmentTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        UnitInventory inventory = AttachEquipmentTalents(owner, talents, catalog, 2, 3);
        ItemCatalog items = catalog.GetSection<ItemCatalog>("item");
        ItemInstance first = CreateInventoryItem(context, owner, items.Require("first-tool"));
        ItemInstance second = CreateInventoryItem(context, owner, items.Require("second-tool"));
        Assert.IsTrue(ChangeInventory(context, owner, first, InventoryOperation.Acquire).Success);
        Assert.IsEmpty(talents.Trees);
        Assert.IsTrue(ChangeInventory(context, owner, first, InventoryOperation.Equip, "main").Success);
        TalentTreeInstance tree = talents.Trees["practice"];
        TalentTreeHandler observer = owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single();
        long epoch = tree.AuthorityVersion;
        for (int index = 0; index < 5; index++) SignalTalent(context, owner, "practice");
        Assert.IsTrue(AllocateTalent(context, tree, "endurance-node").Success);
        AbilityBase original = tree.NodeSources["endurance-node"].Abilities.Single();
        Assert.AreEqual(110, owner.Property.LifePointMax);
        ChangeInventory(context, owner, second, InventoryOperation.Acquire);
        Assert.IsTrue(ChangeInventory(context, owner, second, InventoryOperation.Equip, "main").Success);
        Assert.AreEqual(1, tree.GrantCount);
        Assert.AreEqual(epoch, tree.AuthorityVersion);
        Assert.AreSame(observer, owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single());
        Assert.AreSame(original, tree.NodeSources["endurance-node"].Abilities.Single());
        Assert.IsTrue(ChangeInventory(context, owner, second, InventoryOperation.Unequip).Success);
        Assert.IsFalse(tree.IsAuthorized);
        Assert.AreEqual(100, owner.Property.LifePointMax);
        Assert.IsTrue(ChangeInventory(context, owner, first, InventoryOperation.Equip, "main").Success);
        Assert.AreSame(tree, talents.Trees["practice"]);
        Assert.AreSame(observer, owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single());
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(2, inventory.CarryCount);
        TestContext.WriteLine("Explicit bridge content granted no tree from backpack ownership. Equipping granted one real source; replacing with another grant of the same tree kept its observer, authority epoch and original learned ability. Unequip and re-equip removed and restored that same tree without resetting it.");
    }

    [TestMethod]
    public void ActualEquipmentTradeMovesOnlyTheGrantAndEachOwnersOriginalTreeProgressStaysHome()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        ContentCatalog catalog = LoadEquipmentTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        AbilityCatalog abilities = catalog.GetSection<AbilityCatalog>("ability");
        Unit firstOwner = CreateSourceUnit(context, abilities, "first");
        Unit secondOwner = CreateSourceUnit(context, abilities, "second");
        UnitTalents firstTalents = AttachTalents(firstOwner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        UnitTalents secondTalents = AttachTalents(secondOwner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        UnitInventory firstInventory = AttachEquipmentTalents(firstOwner, firstTalents, catalog, 0, 1);
        UnitInventory secondInventory = AttachEquipmentTalents(secondOwner, secondTalents, catalog, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemCatalog items = catalog.GetSection<ItemCatalog>("item");
        ItemInstance tool = CreateInventoryItem(context, firstOwner, items.Require("first-tool"));
        ChangeInventory(context, firstOwner, tool, InventoryOperation.Acquire, "main");
        TalentTreeInstance firstTree = firstTalents.Trees["practice"];
        for (int index = 0; index < 11; index++) SignalTalent(context, firstOwner, "practice");
        Assert.IsTrue(AllocateTalent(context, firstTree, "endurance-node").Success);
        AbilityBase firstAbility = firstTree.NodeSources["endurance-node"].Abilities.Single();
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(tool.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        Assert.AreEqual(TradeStatus.Completed, ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        TalentTreeInstance secondTree = secondTalents.Trees["practice"];
        Assert.AreSame(tool, secondInventory.Items.Single().Item);
        Assert.AreEqual(secondOwner.Id, tool.OwnerId);
        Assert.IsFalse(firstTree.IsAuthorized);
        Assert.AreEqual(1, firstTree.Ranks["endurance-node"]);
        Assert.AreEqual(1, firstTree.AvailablePoints);
        Assert.AreEqual(new TalentGrowthProgress(1, 2), firstTree.Growth["repetitions"]);
        Assert.AreEqual(0, secondTree.AvailablePoints);
        Assert.IsEmpty(secondTree.Ranks);
        Assert.AreNotSame(firstTree, secondTree);
        Assert.AreEqual(100, firstOwner.Property.LifePointMax);
        Assert.AreEqual(100, secondOwner.Property.LifePointMax);
        for (int index = 0; index < 5; index++) SignalTalent(context, secondOwner, "practice");
        Assert.IsTrue(AllocateTalent(context, secondTree, "endurance-node").Success);
        AbilityBase secondAbility = secondTree.NodeSources["endurance-node"].Abilities.Single();
        Assert.AreNotSame(firstAbility, secondAbility);
        var returning = new TradeSession(secondInventory, firstInventory, (_, _) => true);
        ChangeTrade(context, secondOwner, returning, TradeAction.Offer, [new(tool.InstanceId, "main")]);
        ChangeTrade(context, secondOwner, returning, TradeAction.Confirm);
        Assert.AreEqual(TradeStatus.Completed, ChangeTrade(context, firstOwner, returning, TradeAction.Confirm).Status);
        Assert.AreSame(firstAbility, firstOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.IsFalse(secondTree.IsAuthorized);
        Assert.AreSame(secondAbility, secondTree.NodeSources["endurance-node"].Abilities.Single());
        Assert.AreEqual(110, firstOwner.Property.LifePointMax);
        Assert.AreEqual(100, secondOwner.Property.LifePointMax);
        Assert.AreEqual(new TalentGrowthProgress(1, 2), firstTree.Growth["repetitions"]);
        TestContext.WriteLine("A real two-party gift and return transferred the original item and its grant. Each unit kept its own tree, node ability, points and event remainder; the recipient received neither the donor's learned rank nor its ability object.");
    }

    [TestMethod]
    public void FailingEquipmentProjectionRollsBackInventoryGrantCountsTreeHandlerAndOriginalNodeEffects()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        ContentCatalog catalog = LoadEquipmentTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        UnitInventory inventory = AttachEquipmentTalents(owner, talents, catalog, 1, 2);
        ItemCatalog items = catalog.GetSection<ItemCatalog>("item");
        ItemInstance first = CreateInventoryItem(context, owner, items.Require("first-tool"));
        ItemInstance broken = CreateInventoryItem(context, owner, items.Require("fragile-tool"));
        ChangeInventory(context, owner, first, InventoryOperation.Acquire, "main");
        ChangeInventory(context, owner, broken, InventoryOperation.Acquire);
        TalentTreeInstance tree = talents.Trees["practice"];
        SignalTalent(context, owner, "first");
        AllocateTalent(context, tree, "endurance-node");
        TalentTreeHandler observer = owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single();
        AbilityBase original = tree.NodeSources["endurance-node"].Abilities.Single();
        long treeRevision = tree.Revision;
        long inventoryRevision = inventory.Revision;
        int successes = owner.EventReceiver.History.OfType<InventoryChangeResultEvent>().Count(result => result.Success);
        Assert.ThrowsExactly<ExpressionEvaluationException>(() =>
            ChangeInventory(context, owner, broken, InventoryOperation.Equip, "main"));
        Assert.AreEqual(treeRevision, tree.Revision);
        Assert.AreEqual(inventoryRevision, inventory.Revision);
        Assert.AreEqual(1, tree.GrantCount);
        Assert.AreSame(first, inventory.Items.Single(placement => placement.SlotId == "main").Item);
        Assert.AreSame(observer, owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single());
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.AreEqual(successes, owner.EventReceiver.History.OfType<InventoryChangeResultEvent>().Count(result => result.Success));
        TestContext.WriteLine("The equipment projection failed after preparing the net tree grant replacement: inventory/tree revisions, grant count, observer, learned ability and life effect all remained unchanged with no success notification.");
    }

    [TestMethod]
    public void TwoEquippedTalentGrantsCountRealItemsAndRemovingOneCannotRevokeTheOther()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        ContentCatalog catalog = LoadEquipmentTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "learner");
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        var inventory = new UnitInventory(owner, new InventoryConfiguration(1, 2,
            new Dictionary<string, string> { ["main"] = "weapon", ["secondary"] = "weapon" }),
            catalog.GetSection<EquipmentTalentCatalog>("equipmentTalent").CreatePreparation(talents));
        owner.EventReceiver.AddHandler(new InventoryHandler(inventory));
        ItemCatalog items = catalog.GetSection<ItemCatalog>("item");
        ItemInstance first = CreateInventoryItem(context, owner, items.Require("first-tool"));
        ItemInstance second = CreateInventoryItem(context, owner, items.Require("second-tool"));
        ChangeInventory(context, owner, first, InventoryOperation.Acquire, "main");
        ChangeInventory(context, owner, second, InventoryOperation.Acquire, "secondary");
        TalentTreeInstance tree = talents.Trees["practice"];
        TalentTreeHandler observer = owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single();
        Assert.AreEqual(2, tree.GrantCount);
        long epoch = tree.AuthorityVersion;
        Assert.IsTrue(ChangeInventory(context, owner, first, InventoryOperation.Unequip).Success);
        Assert.AreEqual(1, tree.GrantCount);
        Assert.AreEqual(epoch, tree.AuthorityVersion);
        Assert.AreSame(observer, owner.EventReceiver.GetHandlers<TalentTreeHandler>().Single());
        Assert.IsTrue(ChangeInventory(context, owner, first, InventoryOperation.Discard).Success);
        Assert.AreEqual(1, tree.GrantCount);
        Assert.IsTrue(ChangeInventory(context, owner, second, InventoryOperation.Unequip).Success);
        Assert.AreEqual(0, tree.GrantCount);
        Assert.IsFalse(tree.IsAuthorized);
        Assert.IsEmpty(owner.EventReceiver.GetHandlers<TalentTreeHandler>());
        TestContext.WriteLine("Two equipped originals contributed two distinct grants to one tree. Removing/discarding the first did not remove the second's authorization or observer; only losing the last grant removed observation.");
    }

    [TestMethod]
    [DataRow("{ itemId: missing, treeIds: [neutral] }")]
    [DataRow("{ itemId: tool, treeIds: [missing] }")]
    [DataRow("{ itemId: tool, treeIds: [neutral, neutral] }")]
    public void EquipmentTalentContentRejectsInvalidLinksWithTheirSource(string link)
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("bad-equipment-links.yaml", $"equipmentTalent: [{link}]"),
                    Doc("items.yaml", "item: [{ id: tool, name: Tool }]"),
                    Doc("trees.yaml", "talentTree: [{ id: neutral, name: Neutral }]")],
                [LoadoutContentServiceCollectionExtensions.EquipmentTalentModuleId,
                    LoadoutContentServiceCollectionExtensions.InventoryModuleId,
                    LoadoutContentServiceCollectionExtensions.TalentModuleId]));
        StringAssert.Contains(error.Message, "bad-equipment-links.yaml");
        TestContext.WriteLine(error.Message);
    }

    [TestMethod]
    public void RegisteringBothFeaturesDoesNotImplicitlySelectTheEquipmentTalentBridge()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("unselected-equipment-links.yaml", """
                    equipmentTalent: [{ itemId: tool, treeIds: [neutral] }]
                    item: [{ id: tool, name: Tool }]
                    talentTree: [{ id: neutral, name: Neutral }]
                    """)], [LoadoutContentServiceCollectionExtensions.InventoryModuleId,
                    LoadoutContentServiceCollectionExtensions.TalentModuleId]));
        StringAssert.Contains(error.Message, "equipmentTalent");
        TestContext.WriteLine("Selecting both inventory and talents did not implicitly enable their bridge schema.");
    }

    [TestMethod]
    public void OneEquipmentGrantCanPublishTwoIndependentNeutralTreesInOneCommitWithoutAbilityContent()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        ContentCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load(
            [Doc("neutral-links.yaml", """
                equipmentTalent: [{ itemId: tool, treeIds: [first-tree, second-tree] }]
                item: [{ id: tool, name: Tool, slotType: weapon }]
                talentTree:
                  - { id: first-tree, name: First, initialPoints: 1 }
                  - { id: second-tree, name: Second, initialPoints: 2 }
                """)], [LoadoutContentServiceCollectionExtensions.EquipmentTalentModuleId,
                LoadoutContentServiceCollectionExtensions.InventoryModuleId,
                LoadoutContentServiceCollectionExtensions.TalentModuleId]);
        using var context = new Context();
        context.Logger.Output = null;
        Unit owner = CreateUnit(context, "learner");
        context.AddEventReceiver(owner.EventReceiver);
        UnitTalents talents = AttachTalents(owner, catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray());
        AttachEquipmentTalents(owner, talents, catalog, 0, 1);
        ItemInstance item = CreateInventoryItem(context, owner, catalog.GetSection<ItemCatalog>("item").Require("tool"));
        Assert.IsTrue(ChangeInventory(context, owner, item, InventoryOperation.Acquire, "main").Success);
        Assert.HasCount(2, talents.Trees);
        Assert.HasCount(2, owner.EventReceiver.GetHandlers<TalentTreeHandler>());
        Assert.AreEqual(1, talents.Trees["first-tree"].AvailablePoints);
        Assert.AreEqual(2, talents.Trees["second-tree"].AvailablePoints);
        Assert.IsTrue(talents.Trees.Values.All(tree => tree.GrantCount == 1));
        Assert.IsEmpty(item.Source.Abilities);
        TestContext.WriteLine("One original item's atomic grant published two independent trees/observers with separate point pools, without requiring an ability-content module or inventing abilities.");
    }

    private static UnitInventory AttachEquipmentTalents(
        Unit owner, UnitTalents talents, ContentCatalog catalog, int backpackCapacity, int carryCapacity)
    {
        var inventory = new UnitInventory(owner, new InventoryConfiguration(backpackCapacity, carryCapacity,
            new Dictionary<string, string> { ["main"] = "weapon" }),
            catalog.GetSection<EquipmentTalentCatalog>("equipmentTalent").CreatePreparation(talents));
        owner.EventReceiver.AddHandler(new InventoryHandler(inventory));
        return inventory;
    }

    private static ServiceProvider CreateEquipmentTalentProvider() =>
        new ServiceCollection().AddContentLoading().AddAbilityContent().AddDamageTypeContent()
            .AddInventoryContent().AddTalentContent().AddEquipmentTalentContent().BuildServiceProvider();

    private static ContentCatalog LoadEquipmentTalentContent(ServiceProvider provider) =>
        provider.GetRequiredService<IContentCatalogLoader>().Load(
            [Doc("equipment-links.yaml", EquipmentTalentContent), Doc("talents.yaml", TalentGrowthContent),
                Doc("abilities.yaml", InventoryAbilities)],
            [LoadoutContentServiceCollectionExtensions.EquipmentTalentModuleId,
                LoadoutContentServiceCollectionExtensions.InventoryModuleId,
                LoadoutContentServiceCollectionExtensions.TalentModuleId,
                SkillContentServiceCollectionExtensions.AbilityModuleId,
                SkillContentServiceCollectionExtensions.DamageModuleId]);
}
