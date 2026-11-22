using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Content.Inventory.Model;
using Miaosoft.Sim.Loadout.Content.Registration;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void InventoryContentCompilesForwardReferencesThenCreatesAndEquipsFreshOriginalAbilities()
    {
        using ServiceProvider provider = CreateInventoryContentProvider();
        ContentCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load(
        [
            Doc("items.yaml", """
                item:
                  - id: tool
                    name: Tool
                    slotType: weapon
                    abilities: [{ id: endurance, level: 3 }]
                """),
            Doc("abilities.yaml", InventoryAbilities)
        ], [LoadoutContentServiceCollectionExtensions.InventoryModuleId,
            SkillContentServiceCollectionExtensions.AbilityModuleId,
            SkillContentServiceCollectionExtensions.DamageModuleId]);
        ItemDefinition definition = catalog.GetSection<ItemCatalog>("item").Require("tool");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateSourceUnit(context, catalog.GetSection<AbilityCatalog>("ability"), "owner");
        AttachInventory(owner, 1, 1);
        ItemInstance first = CreateInventoryItem(context, owner, definition);
        ItemInstance second = CreateInventoryItem(context, owner, definition);
        Assert.AreSame(definition, first.Definition);
        Assert.AreSame(definition, second.Definition);
        Assert.AreNotSame(first.Source.Abilities.Single(), second.Source.Abilities.Single());
        Assert.AreEqual(3, first.Source.Abilities.Single().Level);
        Assert.IsNull(first.Source.Holder);
        Assert.IsTrue(ChangeInventory(context, owner, first, InventoryOperation.Acquire, "main").Success);
        Assert.AreEqual(130, owner.Property.LifePointMax);
        Assert.AreSame(first.Source.Abilities.Single(), owner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.IsNull(second.OwnerId);
        TestContext.WriteLine("Item content preceding its ability file compiled once into reusable immutable definitions; runtime creation yielded fresh level-3 originals and equipping applied 130 life maximum.");
    }

    [TestMethod]
    public void NeutralInventoryContentNeedsNeitherAbilityContentNorOtherLoadoutFeatures()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddContentLoading().AddInventoryContent().BuildServiceProvider();
        ContentCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load(
            [Doc("neutral.yaml", "item: [{ id: token, name: Token }]")],
            [LoadoutContentServiceCollectionExtensions.InventoryModuleId]);
        using var context = new Context();
        context.Logger.Output = null;
        Unit owner = CreateUnit(context, "owner");
        context.AddEventReceiver(owner.EventReceiver);
        UnitInventory inventory = AttachInventory(owner, 1, 1);
        ItemInstance token = CreateInventoryItem(context, owner, catalog.GetSection<ItemCatalog>("item").Require("token"));
        Assert.IsEmpty(token.Source.Abilities);
        Assert.IsTrue(ChangeInventory(context, owner, token, InventoryOperation.Acquire).Success);
        Assert.AreSame(token, inventory.Items.Single().Item);
        TestContext.WriteLine("Neutral item content and actual acquisition needed no ability-content module, trading handler or talent feature.");
    }

    [TestMethod]
    [DataRow("{ id: invalid, name: Invalid, abilities: [{ id: missing }] }")]
    [DataRow("{ id: invalid, name: Invalid, abilities: [{ id: endurance, level: 0 }] }")]
    [DataRow("{ id: invalid, name: Invalid, abilities: null }")]
    [DataRow("{ id: invalid, name: Invalid, privateTheme: ignored }")]
    public void InventoryContentRejectsInvalidOrUnknownDefinitionsWithTheirSource(string item)
    {
        using ServiceProvider provider = CreateInventoryContentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("bad-items.yaml", $"item: [{item}]"), Doc("abilities.yaml", InventoryAbilities)],
                [LoadoutContentServiceCollectionExtensions.InventoryModuleId,
                    SkillContentServiceCollectionExtensions.AbilityModuleId,
                    SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "bad-items.yaml");
        TestContext.WriteLine($"Rejected authored item without a success-shaped catalog: {error.Message}");
    }

    [TestMethod]
    public void InventoryContentRejectsDuplicateItemsAcrossDocuments()
    {
        using ServiceProvider provider = CreateInventoryContentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("first.yaml", "item: [{ id: token, name: Token }]"),
                    Doc("duplicate.yaml", "item: [{ id: token, name: Other }]")],
                [LoadoutContentServiceCollectionExtensions.InventoryModuleId]));
        StringAssert.Contains(error.Message, "duplicate.yaml");
        StringAssert.Contains(error.Message, "token");
        TestContext.WriteLine(error.Message);
    }

    [TestMethod]
    public void RegisteredButUnselectedInventoryContentDoesNotExpandTheCoreCatalogSchema()
    {
        using ServiceProvider provider = CreateInventoryContentProvider();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("unselected.yaml", "item: [{ id: token, name: Token }]")], []));
        StringAssert.Contains(error.Message, "unselected.yaml");
        TestContext.WriteLine("DI registration did not silently enable item roots in a catalog that had not selected inventory.");
    }

    private static ServiceProvider CreateInventoryContentProvider() =>
        new ServiceCollection().AddContentLoading().AddAbilityContent().AddDamageTypeContent()
            .AddInventoryContent().BuildServiceProvider();
}
