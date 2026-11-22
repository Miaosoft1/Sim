using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Content.Inventory.Model;
using Miaosoft.Sim.Loadout.Content.Talents.Model;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Model;
using Miaosoft.Sim.Loadout.Trading.Policy;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void DeathTradingRequiresInspectionMovesOriginalAbilitiesAndRetainsRemainderUntilExplicitCleanup()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        Unit actor = CreateSourceUnit(context, catalog, "beneficiary");
        var map = new MapRuntime();
        Place(map, owner, new(10, 10));
        Place(map, actor, new(11, 10));
        UnitInventory inventory = AttachInventory(owner, 1, 2);
        UnitInventory recipient = AttachInventory(actor, 1, 2);
        actor.EventReceiver.AddHandler(new TradeHandler(recipient));
        DeathTradeDirectory directory = AttachDeathTrading(inventory, actor, authorization =>
            authorization.Corpse!.MapRuntime!.TargetingDistance(
                authorization.Corpse.Position, authorization.Beneficiary.MapObject!.Position) <= 2);
        ItemInstance original = CreateInventoryItem(context, owner, new("tool", "Tool", "weapon", [new("endurance", 2)]));
        ItemInstance remainder = CreateInventoryItem(context, owner, new("token", "Token", null, []));
        ChangeInventory(context, owner, original, InventoryOperation.Acquire, "main");
        ChangeInventory(context, owner, remainder, InventoryOperation.Acquire);
        AbilityBase ability = original.Source.Abilities.Single();
        DeathTradeAuthorization authorization = OpenDeathTrading(context, owner, actor, directory);
        Assert.AreEqual(DeathTradeFailure.NotInspected,
            ClaimDeathTrade(context, actor, authorization, 0, [new(original.InstanceId, "main")]).Failure);
        DeathTradeResultEvent inspection = InspectDeathTrade(context, actor, authorization);
        Assert.IsTrue(inspection.Success);
        Assert.HasCount(2, inspection.Items);
        Assert.AreEqual(DeathTradeFailure.None, authorization.Observe().Access);
        Assert.HasCount(2, authorization.Observe().Items);
        Assert.AreEqual(DeathTradeFailure.None,
            ClaimDeathTrade(context, actor, authorization, inspection.InspectionRevision, [new(original.InstanceId, "main")]).Failure);
        Assert.AreSame(original, recipient.Items.Single().Item);
        Assert.AreSame(ability, actor.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(2, ability.Level);
        Assert.AreEqual(120, actor.Property.LifePointMax);
        Assert.AreEqual(100, owner.Property.LifePointMax);
        Assert.AreSame(remainder, inventory.Items.Single().Item);
        Assert.IsFalse(remainder.IsDestroyed);
        Assert.IsFalse(directory.IsClosed);
        Assert.AreSame(authorization, directory.Authorizations[actor.Id]);
        Assert.IsTrue(CleanupDeathTrade(context, directory).Success);
        Assert.IsTrue(directory.IsClosed);
        Assert.IsTrue(remainder.IsDestroyed);
        Assert.IsNull(remainder.OwnerId);
        Assert.IsEmpty(inventory.Items);
        Assert.IsFalse(original.IsDestroyed);
        Assert.AreEqual(actor.Id, original.OwnerId);
        Assert.AreEqual(DeathTradeFailure.Closed, InspectDeathTrade(context, actor, authorization).Failure);
        TestContext.WriteLine("The authorized beneficiary had to inspect in range, then claimed the original level-2 ability with its item. Unclaimed inventory and authorization survived the pickup; explicit cleanup destroyed only the remainder and closed access.");
    }

    [TestMethod]
    public void DeathTradingRechecksLiveRangeAndRejectsRemovedCorpsesEvenWithPermissivePolicy()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        Unit actor = CreateSourceUnit(context, catalog, "beneficiary");
        var map = new MapRuntime();
        Place(map, owner, new(10, 10));
        Place(map, actor, new(11, 10));
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        actor.EventReceiver.AddHandler(new TradeHandler(AttachInventory(actor, 1, 1)));
        bool enforceRange = true;
        DeathTradeDirectory directory = AttachDeathTrading(inventory, actor, authorization => !enforceRange ||
            authorization.Corpse!.MapRuntime!.TargetingDistance(authorization.Corpse.Position, actor.MapObject!.Position) <= 2);
        ItemInstance item = CreateInventoryItem(context, owner, new("token", "Token", null, []));
        ChangeInventory(context, owner, item, InventoryOperation.Acquire);
        DeathTradeAuthorization authorization = OpenDeathTrading(context, owner, actor, directory);
        DeathTradeResultEvent inspected = InspectDeathTrade(context, actor, authorization);
        actor.MapObject!.Position = new(20, 10);
        Assert.AreEqual(DeathTradeFailure.PolicyRejected, authorization.Observe().Access);
        Assert.HasCount(0, authorization.Observe().Items);
        Assert.AreEqual(DeathTradeFailure.PolicyRejected,
            ClaimDeathTrade(context, actor, authorization, inspected.InspectionRevision, [new(item.InstanceId)]).Failure);
        Assert.AreEqual(owner.Id, item.OwnerId);
        enforceRange = false;
        map.RemoveObject(owner.MapObject!);
        Assert.AreEqual(DeathTradeFailure.CorpseUnavailable, authorization.Observe().Access);
        Assert.HasCount(0, authorization.Observe().Items);
        Assert.AreEqual(DeathTradeFailure.CorpseUnavailable, InspectDeathTrade(context, actor, authorization).Failure);
        Assert.AreEqual(DeathTradeFailure.CorpseUnavailable,
            ClaimDeathTrade(context, actor, authorization, inspected.InspectionRevision, [new(item.InstanceId)]).Failure);
        Assert.IsFalse(directory.IsClosed);
        Assert.AreSame(item, inventory.Items.Single().Item);
        TestContext.WriteLine("A previously valid inspection did not authorize remote pickup after movement. Removing the exact corpse blocked both inspection and claim even when the supplied range policy returned true; retained inventory was not silently destroyed.");
    }

    [TestMethod]
    public void DeathTradingBindsInspectionToBothInventoriesAndRejectsUnauthorizedItemsAndCapacityOverflow()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        Unit actor = CreateSourceUnit(context, catalog, "beneficiary");
        var map = new MapRuntime();
        Place(map, owner, new(10, 10));
        Place(map, actor, new(11, 10));
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        UnitInventory recipient = AttachInventory(actor, 1, 1);
        actor.EventReceiver.AddHandler(new TradeHandler(recipient));
        DeathTradeDirectory directory = AttachDeathTrading(inventory, actor, _ => true);
        ItemInstance item = CreateInventoryItem(context, owner, new("token", "Token", null, []));
        ItemInstance blocker = CreateInventoryItem(context, actor, new("blocker", "Blocker", null, []));
        ChangeInventory(context, owner, item, InventoryOperation.Acquire);
        DeathTradeAuthorization authorization = OpenDeathTrading(context, owner, actor, directory);
        DeathTradeResultEvent initial = InspectDeathTrade(context, actor, authorization);
        Assert.AreEqual(DeathTradeFailure.InvalidSelection,
            ClaimDeathTrade(context, actor, authorization, initial.InspectionRevision, [new(blocker.InstanceId)]).Failure);
        ChangeInventory(context, actor, blocker, InventoryOperation.Acquire);
        Assert.AreEqual(DeathTradeFailure.InventoryChanged, authorization.Observe().Access);
        Assert.HasCount(0, authorization.Observe().Items);
        Assert.AreEqual(DeathTradeFailure.InventoryChanged,
            ClaimDeathTrade(context, actor, authorization, initial.InspectionRevision, [new(item.InstanceId)]).Failure);
        DeathTradeResultEvent full = InspectDeathTrade(context, actor, authorization);
        Assert.AreEqual(DeathTradeFailure.InvalidLayout,
            ClaimDeathTrade(context, actor, authorization, full.InspectionRevision, [new(item.InstanceId)]).Failure);
        Assert.AreEqual(owner.Id, item.OwnerId);
        ChangeInventory(context, actor, blocker, InventoryOperation.Discard);
        DeathTradeResultEvent refreshed = InspectDeathTrade(context, actor, authorization);
        Assert.IsTrue(ClaimDeathTrade(context, actor, authorization, refreshed.InspectionRevision, [new(item.InstanceId)]).Success);
        Assert.AreEqual(DeathTradeFailure.StaleInspection,
            ClaimDeathTrade(context, actor, authorization, refreshed.InspectionRevision, [new(item.InstanceId)]).Failure);
        Assert.AreSame(item, recipient.Items.Single().Item);
        TestContext.WriteLine("Inspection bound both layout revisions and the exact authorized IDs. Foreign IDs, changed inventory, over-capacity pickup and replayed consent each failed explicitly without partial transfer.");
    }

    [TestMethod]
    public void OldDeathAuthorizationCannotBecomeValidAgainAfterReviveAndAnotherDeathBeforeNewPublication()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        Unit actor = CreateSourceUnit(context, catalog, "beneficiary");
        var map = new MapRuntime();
        Place(map, owner, new(10, 10));
        Place(map, actor, new(11, 10));
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        actor.EventReceiver.AddHandler(new TradeHandler(AttachInventory(actor, 1, 1)));
        DeathTradeDirectory directory = AttachDeathTrading(inventory, actor, _ => true);
        ItemInstance item = CreateInventoryItem(context, owner, new("token", "Token", null, []));
        ChangeInventory(context, owner, item, InventoryOperation.Acquire);
        DeathTradeAuthorization old = OpenDeathTrading(context, owner, actor, directory);
        context.Dispatch(context.Factory.NewEvent<UnitReviveRequestEvent>() with
        {
            Unit = owner, RestoreLifePoint = 50, Receiver = owner.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        Assert.IsTrue(owner.IsAlive);
        Assert.IsNull(owner.CurrentDeathId);
        Assert.AreEqual(DeathTradeFailure.NotAuthorized, InspectDeathTrade(context, actor, old).Failure);
        owner.Killed();
        Guid nextDeath = owner.CurrentDeathId!.Value;
        Assert.AreNotEqual(old.DeathId, owner.CurrentDeathId);
        Assert.AreEqual(old.DeathId, directory.DeathId);
        Assert.AreEqual(DeathTradeFailure.NotAuthorized, InspectDeathTrade(context, actor, old).Failure);
        PublishDeathTrade(context, owner, actor, nextDeath);
        DeathTradeAuthorization current = directory.Authorizations[actor.Id];
        Assert.AreNotSame(old, current);
        Assert.IsTrue(InspectDeathTrade(context, actor, current).Success);
        Assert.AreEqual(DeathTradeFailure.NotAuthorized, InspectDeathTrade(context, actor, old).Failure);
        TestContext.WriteLine("The actual unit death ID changed at death commit, before a replacement authorization was published. Old claims stayed invalid across revival and the next death; only the newly distributed capability could inspect.");
    }

    [TestMethod]
    public void DeathPickupProjectionFailurePreservesBothInventoriesOriginalSourceAndInspectionForRetry()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: recipient-sensitive
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "10 / (target.level - 2)" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        Unit actor = CreateSourceUnit(context, catalog, "beneficiary");
        RequestUnitLevelUp(context, actor, 1, 2);
        var map = new MapRuntime();
        Place(map, owner, new(10, 10));
        Place(map, actor, new(11, 10));
        UnitInventory inventory = AttachInventory(owner, 0, 1);
        UnitInventory recipient = AttachInventory(actor, 0, 1);
        actor.EventReceiver.AddHandler(new TradeHandler(recipient));
        DeathTradeDirectory directory = AttachDeathTrading(inventory, actor, _ => true);
        ItemInstance item = CreateInventoryItem(context, owner, new("tool", "Tool", "weapon", [new("recipient-sensitive")]));
        ChangeInventory(context, owner, item, InventoryOperation.Acquire, "main");
        AbilityBase original = item.Source.Abilities.Single();
        DeathTradeAuthorization authorization = OpenDeathTrading(context, owner, actor, directory);
        DeathTradeResultEvent inspected = InspectDeathTrade(context, actor, authorization);
        long corpseRevision = inventory.Revision;
        long actorRevision = recipient.Revision;
        long inspectionRevision = authorization.Revision;
        Assert.ThrowsExactly<ExpressionEvaluationException>(() =>
            ClaimDeathTrade(context, actor, authorization, inspected.InspectionRevision, [new(item.InstanceId, "main")]));
        Assert.AreEqual(corpseRevision, inventory.Revision);
        Assert.AreEqual(actorRevision, recipient.Revision);
        Assert.AreEqual(inspectionRevision, authorization.Revision);
        Assert.IsTrue(authorization.IsInspected);
        Assert.AreEqual(owner.Id, item.OwnerId);
        Assert.AreSame(owner, item.Source.Holder);
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        RequestUnitLevelUp(context, actor, 2, 3);
        Assert.IsTrue(ClaimDeathTrade(context, actor, authorization, inspected.InspectionRevision, [new(item.InstanceId, "main")]).Success);
        Assert.AreSame(original, actor.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, actor.Property.LifePointMax);
        TestContext.WriteLine("The recipient's formula rejected the staged original item. Both layouts, item/source owner, original handler and inspected consent remained unchanged; a valid later projection used the same original instance and inspection.");
    }

    [TestMethod]
    public void DeathPickupUsesTheSameAtomicEquipmentGrantBridgeWithoutTransferringRoleProgress()
    {
        using ServiceProvider provider = CreateEquipmentTalentProvider();
        var catalog = LoadEquipmentTalentContent(provider);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "owner");
        Unit actor = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "beneficiary");
        var map = new MapRuntime();
        Place(map, owner, new(10, 10));
        Place(map, actor, new(11, 10));
        var definitions = catalog.GetSection<TalentTreeCatalog>("talentTree").Definitions.Values.ToArray();
        UnitTalents ownerTalents = AttachTalents(owner, definitions);
        UnitTalents actorTalents = AttachTalents(actor, definitions);
        UnitInventory inventory = AttachEquipmentTalents(owner, ownerTalents, catalog, 0, 1);
        UnitInventory recipient = AttachEquipmentTalents(actor, actorTalents, catalog, 0, 1);
        actor.EventReceiver.AddHandler(new TradeHandler(recipient));
        DeathTradeDirectory directory = AttachDeathTrading(inventory, actor, _ => true);
        ItemInstance item = CreateInventoryItem(context, owner, catalog.GetSection<ItemCatalog>("item").Require("first-tool"));
        ChangeInventory(context, owner, item, InventoryOperation.Acquire, "main");
        SignalTalent(context, owner, "first");
        TalentTreeInstance originalTree = ownerTalents.Trees["practice"];
        AllocateTalent(context, originalTree, "endurance-node");
        AbilityBase originalAbility = originalTree.NodeSources["endurance-node"].Abilities.Single();
        DeathTradeAuthorization authorization = OpenDeathTrading(context, owner, actor, directory);
        DeathTradeResultEvent inspected = InspectDeathTrade(context, actor, authorization);
        Assert.IsTrue(ClaimDeathTrade(context, actor, authorization, inspected.InspectionRevision, [new(item.InstanceId, "main")]).Success);
        Assert.IsFalse(originalTree.IsAuthorized);
        Assert.AreSame(originalAbility, originalTree.NodeSources["endurance-node"].Abilities.Single());
        Assert.AreEqual(1, originalTree.Ranks["endurance-node"]);
        Assert.AreEqual(2, originalTree.AvailablePoints);
        TalentTreeInstance actorTree = actorTalents.Trees["practice"];
        Assert.AreNotSame(originalTree, actorTree);
        Assert.IsTrue(actorTree.IsAuthorized);
        Assert.AreEqual(0, actorTree.AvailablePoints);
        Assert.IsEmpty(actorTree.Ranks);
        Assert.AreEqual(100, actor.Property.LifePointMax);
        TestContext.WriteLine("Authorized corpse pickup joined both equipment bridges in the original-item transfer commit. The dead role retained its learned original ability and remaining points; the beneficiary received only its own fresh tree authorization.");
    }

    private sealed class SpecifiedDeathBeneficiary(Unit beneficiary) : IDeathTradeDistributionPolicy
    {
        public IEnumerable<DeathTradeAllocation> Allocate(DeathResolvedEvent death, IReadOnlyList<ItemInstance> items)
        {
            yield return new(beneficiary, items.Select(item => item.InstanceId).ToArray());
        }
    }

    private static DeathTradeDirectory AttachDeathTrading(
        UnitInventory inventory, Unit beneficiary, Func<DeathTradeAuthorization, bool> canInteract)
    {
        var directory = new DeathTradeDirectory(inventory, new SpecifiedDeathBeneficiary(beneficiary), canInteract);
        inventory.Owner.EventReceiver.AddHandler(new DeathTradeHandler(directory));
        return directory;
    }

    private static DeathTradeAuthorization OpenDeathTrading(Context context, Unit owner, Unit beneficiary, DeathTradeDirectory directory)
    {
        owner.Killed();
        Guid id = owner.CurrentDeathId!.Value;
        PublishDeathTrade(context, owner, beneficiary, id);
        return directory.Authorizations[beneficiary.Id];
    }

    private static void PublishDeathTrade(Context context, Unit owner, Unit beneficiary, Guid deathId)
    {
        context.Dispatch(context.Factory.NewEvent<DeathResolvedEvent>() with
        {
            DeathId = deathId, Victim = owner, Killer = beneficiary,
            TargetObject = owner.MapObject, Receiver = owner.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
    }

    private static DeathTradeResultEvent InspectDeathTrade(Context context, Unit actor, DeathTradeAuthorization authorization)
    {
        var request = context.Factory.NewEvent<DeathTradeInspectRequestEvent>() with
        {
            Actor = actor, Authorization = authorization, Receiver = actor.EventReceiver
        };
        context.Dispatch(request).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return actor.EventReceiver.History.OfType<DeathTradeResultEvent>().Single(result => result.OperationId == request.OperationId);
    }

    private static DeathTradeResultEvent ClaimDeathTrade(
        Context context, Unit actor, DeathTradeAuthorization authorization, long inspectionRevision,
        IReadOnlyList<TradeItemOffer> items)
    {
        var request = context.Factory.NewEvent<DeathTradeClaimRequestEvent>() with
        {
            Actor = actor, Authorization = authorization, ExpectedInspectionRevision = inspectionRevision,
            Items = items, Receiver = actor.EventReceiver
        };
        context.Dispatch(request).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return actor.EventReceiver.History.OfType<DeathTradeResultEvent>().Single(result => result.OperationId == request.OperationId);
    }

    private static DeathTradeResultEvent CleanupDeathTrade(Context context, DeathTradeDirectory directory)
    {
        var request = context.Factory.NewEvent<DeathTradeCleanupRequestEvent>() with
        {
            Directory = directory, ExpectedDeathId = directory.DeathId!.Value, Receiver = directory.Inventory.Owner.EventReceiver
        };
        context.Dispatch(request).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return directory.Inventory.Owner.EventReceiver.History.OfType<DeathTradeResultEvent>()
            .Single(result => result.OperationId == request.OperationId);
    }
}
