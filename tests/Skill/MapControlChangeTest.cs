using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class MapControlChangeTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ControlChangeStopsOnlyTheReceivingUnitsMovement()
    {
        using var context = new Context();
        context.Logger.Output = null;
        Unit unit = CreateUnit(context);
        Unit other = CreateUnit(context);
        var map = new MapRuntime(100, 100);
        map.AddObject(unit.MapObject!);
        context.Dispatch(context.Factory.NewEvent<UnitMoveAuthorizedEvent>() with
        {
            Source = unit,
            Destination = new Vector2D(10, 0),
            Receiver = unit.EventReceiver
        }).ToList();
        context.RunUntil(TimeSpan.Zero);
        MovementState movement = unit.MapObject!.Movement!;
        Assert.IsNull(movement.EndReason);

        context.Dispatch(context.Factory.NewEvent<UnitMapControlChangedEvent>() with
        {
            Unit = other,
            Receiver = unit.EventReceiver
        }).ToList();
        context.RunUntil(TimeSpan.Zero);
        Assert.IsNull(movement.EndReason);

        context.Dispatch(context.Factory.NewEvent<UnitMapControlChangedEvent>() with
        {
            Unit = unit,
            Receiver = unit.EventReceiver
        }).ToList();
        context.RunUntil(TimeSpan.Zero);
        Assert.AreEqual(MovementEndReason.MapChanged, movement.EndReason);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(new Vector2D(0, 0), unit.MapObject.Position);
        TestContext.WriteLine("A map-control change stopped the matching unit without affecting an unrelated unit.");
    }

    private static Unit CreateUnit(Context context)
    {
        var unit = new Unit
        {
            Name = "unit",
            ArchetypeId = "unit",
            Property = new UnitProperty { LifePoint = 100, LifePointMax = 100, MoveSpeedBase = 1 },
            Abilities = new UnitAbilityManager(),
            EventReceiver = context.Factory.NewEventReceiver()
        };
        unit.EventReceiver.AddHandler(
            new AbilityInstallHandler(), new UnitActionGateHandler(),
            new UnitMovementHandler(), new MapMovementHandler());
        context.AddEventReceiver(unit.EventReceiver);
        unit.Initialize();
        unit.MapObjects.Initialize(new MapObject(unit.MapObjects.UnitId, new Vector2D(0, 0), unit.EventReceiver));
        return unit;
    }
}
