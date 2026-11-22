using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class UnitPropertyTest
{
    [TestMethod]
    public void SharedAbilityManagerIsRejectedBeforeInitializingSecondUnit()
    {
        using var context = new Context();
        var manager = new UnitAbilityManager();
        var first = new Unit
        {
            Name = "first", ArchetypeId = "first", Abilities = manager,
            EventReceiver = context.Factory.NewEventReceiver()
        };
        var second = new Unit
        {
            Name = "second", ArchetypeId = "second", Abilities = manager,
            EventReceiver = context.Factory.NewEventReceiver()
        };
        first.Initialize();
        Assert.ThrowsExactly<InvalidOperationException>(second.Initialize);
        Assert.IsEmpty(second.EventReceiver.GetHandlers<IHandler>());
        Assert.AreSame(first.EventReceiver, manager.EventReceiver);
    }

    [TestMethod]
    public void PassiveProjectionRequiresInitializedOwner()
    {
        using var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        receiver.AddHandler(new PropertyAbility { Id = "passive" });
        var manager = new UnitAbilityManager { EventReceiver = receiver };
        UnitProperty original = manager.Property;

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => manager.ReApply());

        StringAssert.Contains(error.Message, "initialized owner");
        Assert.AreSame(original, manager.Property);
        TestContext.WriteLine("An uninitialized passive owner fails without publishing a property projection.");
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void UnitProperty_MoveSpeed_PropertiesSetAndGet()
    {
        UnitProperty property = new()
        {
            MoveSpeedBase = 325,
            MoveSpeed = 370
        };

        Assert.AreEqual(325, property.MoveSpeedBase);
        Assert.AreEqual(370, property.MoveSpeed);
    }

    [TestMethod]
    public void UnitProperty_Copy_ProducesEquivalentIndependentInstance()
    {
        UnitProperty original = new()
        {
            Level = 5,
            LifePoint = 950,
            LifePointMax = 1000,
            MoveSpeedBase = 330,
            MoveSpeed = 350
        };

        UnitProperty copy = original.Copy();

        Assert.AreEqual(original.Level, copy.Level);
        Assert.AreEqual(original.LifePoint, copy.LifePoint);
        Assert.AreEqual(original.LifePointMax, copy.LifePointMax);
        Assert.AreEqual(original.MoveSpeedBase, copy.MoveSpeedBase);
        Assert.AreEqual(original.MoveSpeed, copy.MoveSpeed);

        copy.LifePoint += 50;
        Assert.AreNotEqual(original.LifePoint, copy.LifePoint, "Modifying copy must not mutate original.");
    }

    [TestMethod]
    public void UnitProperty_DirectProperties_StoresAndModifiesDirectly()
    {
        UnitProperty prop = new();

        prop.LifePoint = 500;
        prop.LifePointMax = 1000;
        prop.MoveSpeed = 300;
        prop.Level = 2;

        Assert.AreEqual(500, prop.LifePoint);
        Assert.AreEqual(1000, prop.LifePointMax);
        Assert.AreEqual(300, prop.MoveSpeed);
        Assert.AreEqual(2, prop.Level);

        UnitProperty cloned = prop.Copy();
        cloned.LifePoint = 999;
        Assert.AreEqual(500, prop.LifePoint, "Cloned properties must be independent.");
    }

    [TestMethod]
    public void NumericExtensionsAreFiniteAndCopiedIndependently()
    {
        var property = new UnitProperty();
        property.AddNumericExtension("damage.armor.Light", 10);
        UnitProperty copy = property.Copy();
        copy.AddNumericExtension("damage.armor.Light", -15);
        Assert.AreEqual(10d, property.GetNumericExtension("damage.armor.Light"));
        Assert.AreEqual(-5d, copy.GetNumericExtension("damage.armor.Light"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => property.AddNumericExtension("damage.armor.Light", double.PositiveInfinity));
    }
}
