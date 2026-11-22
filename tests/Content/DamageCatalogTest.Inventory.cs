using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    private const string InventoryAbilities = """
        ability:
          - id: endurance
            type: BaseProperty
            allowMultiple: false
            trigger: { type: None }
            data: { modifiers: [{ target: lifePointMax, formula: "ability.level * 10" }] }
          - id: fragile
            type: BaseProperty
            allowMultiple: false
            trigger: { type: None }
            data: { modifiers: [{ target: lifePointMax, formula: "10 / (2 - ability.level)" }] }
        """;

    [TestMethod]
    public void InventoryEquipmentReplacementUsesNetCapacityAndRetainsOriginalAbilityObjects()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        UnitInventory inventory = AttachInventory(owner, 1, 2);
        ItemInstance first = CreateInventoryItem(context, owner, new("first", "First", "weapon", [new("endurance", 1)]));
        ItemInstance second = CreateInventoryItem(context, owner, new("second", "Second", "weapon", [new("endurance", 2)]));
        InventoryChangeResultEvent Change(ItemInstance item, InventoryOperation operation, string? slot = null) =>
            ChangeInventory(context, owner, item, operation, slot);
        Assert.IsTrue(Change(first, InventoryOperation.Acquire, "main").Success);
        AbilityBase firstAbility = first.Source.Abilities.Single();
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.IsTrue(Change(second, InventoryOperation.Acquire).Success);
        AbilityBase secondAbility = second.Source.Abilities.Single();
        Assert.IsNull(second.Source.Holder);
        Assert.AreEqual(2, inventory.CarryCount);
        Assert.AreEqual(1, inventory.BackpackCount);
        Assert.IsTrue(Change(second, InventoryOperation.Equip, "main").Success);
        Assert.AreEqual(120, owner.Property.LifePointMax);
        Assert.AreEqual("main", inventory.Items.Single(placement => placement.Item == second).SlotId);
        Assert.IsNull(inventory.Items.Single(placement => placement.Item == first).SlotId);
        Assert.AreSame(secondAbility, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreSame(firstAbility, first.Source.Abilities.Single());
        Assert.IsNull(first.Source.Holder);
        Assert.AreEqual(InventoryChangeFailure.CapacityExceeded, Change(second, InventoryOperation.Unequip).Failure);
        Assert.AreEqual(120, owner.Property.LifePointMax);
        Assert.IsTrue(Change(first, InventoryOperation.Discard).Success);
        Assert.IsTrue(first.IsDestroyed);
        Assert.IsNull(first.OwnerId);
        Assert.IsTrue(Change(second, InventoryOperation.Unequip).Success);
        Assert.AreEqual(100, owner.Property.LifePointMax);
        Assert.IsTrue(Change(second, InventoryOperation.Equip, "main").Success);
        Assert.AreSame(secondAbility, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(120, owner.Property.LifePointMax);
        Assert.AreEqual(InventoryChangeFailure.AlreadyOwnedOrDestroyed, Change(first, InventoryOperation.Acquire).Failure);
        TestContext.WriteLine("A full backpack accepted a net-zero replacement, rejected an over-capacity unequip, and retained both original source abilities; discarded items could not be reacquired.");
    }

    [TestMethod]
    public void InventoryProjectionFailureRollsBackSlotsOwnershipSourcesAndSuccessNotifications()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        UnitInventory inventory = AttachInventory(owner, 1, 2);
        ItemInstance first = CreateInventoryItem(context, owner, new("first", "First", "weapon", [new("endurance")]));
        ItemInstance fragile = CreateInventoryItem(context, owner, new("fragile", "Fragile", "weapon", [new("fragile", 2)]));
        ChangeInventory(context, owner, first, InventoryOperation.Acquire, "main");
        ChangeInventory(context, owner, fragile, InventoryOperation.Acquire);
        AbilityBase original = first.Source.Abilities.Single();
        AbilityBase candidate = fragile.Source.Abilities.Single();
        long revision = inventory.Revision;
        long firstRevision = first.Revision;
        long candidateRevision = fragile.Revision;
        int successes = owner.EventReceiver.History.OfType<InventoryChangeResultEvent>().Count(result => result.Success);
        Assert.ThrowsExactly<ExpressionEvaluationException>(() =>
            ChangeInventory(context, owner, fragile, InventoryOperation.Equip, "main"));
        Assert.AreEqual(revision, inventory.Revision);
        Assert.AreEqual(firstRevision, first.Revision);
        Assert.AreEqual(candidateRevision, fragile.Revision);
        Assert.AreEqual("main", inventory.Items.Single(placement => placement.Item == first).SlotId);
        Assert.IsNull(inventory.Items.Single(placement => placement.Item == fragile).SlotId);
        Assert.AreSame(owner, first.Source.Holder);
        Assert.IsNull(fragile.Source.Holder);
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, owner.Property.LifePointMax);
        Assert.AreEqual(successes, owner.EventReceiver.History.OfType<InventoryChangeResultEvent>().Count(result => result.Success));
        ChangeSources(context, owner, levels: [new(fragile.Source, candidate.RuntimeInstanceId, 1)]);
        Assert.IsTrue(ChangeInventory(context, owner, fragile, InventoryOperation.Equip, "main").Success);
        Assert.AreSame(candidate, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        TestContext.WriteLine("A failing replacement left inventory/item revisions, slot, source authority, handlers and success notifications unchanged; repairing the same candidate allowed a retry.");
    }

    [TestMethod]
    public void QueuedInventoryOperationsPrepareAgainstCommittedStateAndRejectExplicitStaleRevisions()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        UnitInventory inventory = AttachInventory(owner, 2, 2);
        ItemInstance first = CreateInventoryItem(context, owner, new("neutral", "Neutral", null, []));
        ItemInstance second = CreateInventoryItem(context, owner, new("neutral", "Neutral", null, []));
        foreach (ItemInstance item in new[] { first, second })
            context.Dispatch(context.Factory.NewEvent<InventoryChangeRequestEvent>() with
            {
                Owner = owner, Item = item, Operation = InventoryOperation.Acquire, Receiver = owner.EventReceiver
            }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(2, inventory.CarryCount);
        Assert.IsTrue(first.Source.IsMaterialized);
        Assert.IsTrue(second.Source.IsMaterialized);
        Assert.IsEmpty(first.Source.Abilities);
        Assert.IsEmpty(second.Source.Abilities);
        ItemInstance third = CreateInventoryItem(context, owner, new("neutral", "Neutral", null, []));
        InventoryChangeResultEvent rejected = ChangeInventory(
            context, owner, third, InventoryOperation.Acquire, expectedRevision: 0);
        Assert.AreEqual(InventoryChangeFailure.StaleRevision, rejected.Failure);
        Assert.AreEqual(InventoryChangeFailure.CapacityExceeded,
            ChangeInventory(context, owner, third, InventoryOperation.Acquire).Failure);
        Assert.IsNull(third.OwnerId);
        Assert.IsTrue(third.Source.IsMaterialized);
        Assert.AreEqual(2, inventory.CarryCount);
        Assert.HasCount(2, owner.EventReceiver.History.OfType<InventoryChangeResultEvent>().Where(result => result.Success));
        TestContext.WriteLine("Two same-timestamp requests prepared inside their serialized commit events and both succeeded; explicit stale revision and final capacity were ordinary rejections, not context faults.");
    }

    [TestMethod]
    public void InventoryCompletionStillPublishesAfterACommittedSourceObserverFailure()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        ItemInstance item = CreateInventoryItem(context, owner, new("first", "First", "weapon", [new("endurance")]));
        Guid operationId = Guid.NewGuid();
        bool failed = false;
        Action<IEvent> observer = ev =>
        {
            if (ev is AbilitySourcesChangedEvent && !failed)
            {
                failed = true;
                throw new InvalidOperationException("source publication failed after inventory commit");
            }
        };
        context.EventQueued += observer;
        context.Dispatch(context.Factory.NewEvent<InventoryChangeRequestEvent>() with
        {
            Owner = owner, Item = item, Operation = InventoryOperation.Acquire,
            SlotId = "main", OperationId = operationId, Receiver = owner.EventReceiver
        }).ToList();
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        context.EventQueued -= observer;
        Assert.AreEqual(operationId, error.OperationId);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(1, inventory.CarryCount);
        Assert.AreEqual(owner.Id, item.OwnerId);
        Assert.AreSame(owner, item.Source.Holder);
        Assert.AreEqual(110, owner.Property.LifePointMax);
        InventoryChangeResultEvent result = owner.EventReceiver.History.OfType<InventoryChangeResultEvent>().Single();
        Assert.IsTrue(result.Success);
        Assert.AreEqual(operationId, result.CommittedOperationId);
        TestContext.WriteLine("A committed source publication failure preserved the inventory and capability and still delivered the inventory success with its committed operation ID.");
    }

    [TestMethod]
    public void CreatedItemsAlreadyOwnFreshRealAbilitiesBeforeAnyInventoryOrActivationAuthorization()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        var definition = new ItemDefinition("first", "First", "weapon", [new("endurance", 2)]);
        ItemInstance first = CreateInventoryItem(context, owner, definition);
        ItemInstance second = CreateInventoryItem(context, owner, definition);
        AbilityBase original = first.Source.Abilities.Single();
        Assert.AreEqual(2, original.Level);
        Assert.AreNotSame(original, second.Source.Abilities.Single());
        Assert.AreNotEqual(original.RuntimeInstanceId, second.Source.Abilities.Single().RuntimeInstanceId);
        Assert.AreSame(context, first.Source.BoundContext);
        Assert.IsNull(first.Source.Holder);
        Assert.IsNull(first.OwnerId);
        Assert.IsEmpty(inventory.Items);
        Assert.IsEmpty(owner.Abilities.Sources);
        Assert.IsEmpty(owner.EventReceiver.GetHandlers<PropertyAbility>());
        Assert.AreEqual(100, owner.Property.LifePointMax);
        ChangeInventory(context, owner, first, InventoryOperation.Acquire, "main");
        Assert.AreSame(original, owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(120, owner.Property.LifePointMax);
        Assert.IsNull(second.OwnerId);
        TestContext.WriteLine("Creation, not acquisition, established distinct real level-2 abilities and their context; neither owned inventory nor activation until the original instance was acquired.");
    }

    [TestMethod]
    public void FailedItemCreationDoesNotExposeAHalfCreatedInstanceOrChangeInventory()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        Guid operationId = Guid.NewGuid();
        context.Dispatch(context.Factory.NewEvent<ItemCreateRequestEvent>() with
        {
            Coordinator = owner, Definition = new("bad", "Bad", "weapon", [new("missing")]),
            OperationId = operationId, Receiver = owner.EventReceiver
        }).ToList();
        Assert.ThrowsExactly<KeyNotFoundException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.IsEmpty(owner.EventReceiver.History.OfType<ItemCreatedEvent>());
        Assert.IsEmpty(inventory.Items);
        Assert.AreEqual(0, inventory.Revision);
        Assert.IsEmpty(owner.Abilities.Sources);
        ItemInstance valid = CreateInventoryItem(context, owner, new("valid", "Valid", "weapon", [new("endurance")]));
        Assert.IsTrue(valid.Source.IsMaterialized);
        Assert.IsNull(valid.OwnerId);
        TestContext.WriteLine("A missing ability failed actual item creation without exposing a partial instance, inventory changes or success; subsequent valid creation still succeeded.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreatedItemsCannotEnterAnotherContextEvenBeforeTheyHaveEverBeenAcquired(bool hasAbility)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var firstContext = new Context(causalityFactory: new TriggerChainFactory());
        using var secondContext = new Context(causalityFactory: new TriggerChainFactory());
        firstContext.Logger.Output = secondContext.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(firstContext, catalog, "first");
        Unit secondOwner = CreateSourceUnit(secondContext, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 1, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 1, 1);
        ItemInstance item = CreateInventoryItem(firstContext, firstOwner,
            new("bound", "Bound", "weapon", hasAbility ? [new("endurance")] : []));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ChangeInventory(secondContext, secondOwner, item, InventoryOperation.Acquire));
        Assert.IsEmpty(secondInventory.Items);
        Assert.IsNull(item.OwnerId);
        Assert.AreSame(firstContext, item.Source.BoundContext);
        Assert.IsTrue(ChangeInventory(firstContext, firstOwner, item, InventoryOperation.Acquire).Success);
        Assert.AreSame(item, firstInventory.Items.Single().Item);
        TestContext.WriteLine($"An unacquired item with abilities={hasAbility} retained its creation context across a rejected cross-context acquisition.");
    }

    [TestMethod]
    public void CreatingOrCarryingAnUnheldItemDoesNotResampleTheCoordinatorsExistingProjection()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "target.life" }] }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog, "owner");
        AttachInventory(owner, 1, 2);
        ItemInstance equipped = CreateInventoryItem(context, owner, new("equipped", "Equipped", "weapon", [new("endurance")]));
        ChangeInventory(context, owner, equipped, InventoryOperation.Acquire, "main");
        Assert.AreEqual(200, owner.Property.LifePointMax);
        owner.Property.LifePoint = 40;
        ItemInstance neutral = CreateInventoryItem(context, owner, new("neutral", "Neutral", null, []));
        Assert.AreEqual(200, owner.Property.LifePointMax);
        ChangeInventory(context, owner, neutral, InventoryOperation.Acquire);
        Assert.AreEqual(200, owner.Property.LifePointMax);
        owner.Abilities.ReApply();
        Assert.AreEqual(140, owner.Property.LifePointMax);
        TestContext.WriteLine("Unheld item creation and backpack acquisition did not reevaluate the coordinator's life-dependent modifier; a real owner projection still resampled 200 to 140.");
    }

    private static ItemInstance CreateInventoryItem(Context context, Unit coordinator, ItemDefinition definition)
    {
        Guid operationId = Guid.NewGuid();
        context.Dispatch(context.Factory.NewEvent<ItemCreateRequestEvent>() with
        {
            Coordinator = coordinator, Definition = definition, OperationId = operationId,
            Receiver = coordinator.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return coordinator.EventReceiver.History.OfType<ItemCreatedEvent>()
            .Single(result => result.OperationId == operationId).Item;
    }

    private static UnitInventory AttachInventory(Unit owner, int backpackCapacity, int carryCapacity)
    {
        var inventory = new UnitInventory(owner, new InventoryConfiguration(
            backpackCapacity, carryCapacity, new Dictionary<string, string> { ["main"] = "weapon" }));
        owner.EventReceiver.AddHandler(new InventoryHandler(inventory));
        return inventory;
    }

    private static InventoryChangeResultEvent ChangeInventory(
        Context context, Unit owner, ItemInstance item, InventoryOperation operation, string? slotId = null,
        long? expectedRevision = null)
    {
        Guid operationId = Guid.NewGuid();
        context.Dispatch(context.Factory.NewEvent<InventoryChangeRequestEvent>() with
        {
            Owner = owner, Item = item, Operation = operation, SlotId = slotId,
            ExpectedRevision = expectedRevision, OperationId = operationId, Receiver = owner.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return owner.EventReceiver.History.OfType<InventoryChangeResultEvent>()
            .Single(result => result.OperationId == operationId);
    }
}
