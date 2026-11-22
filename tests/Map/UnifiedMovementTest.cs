using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.EventData;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class UnifiedMovementTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(30d, 1)]
    [DataRow(60d, 2)]
    [DataRow(120d, 3)]
    public void PlayerAndProjectileShareEveryIntermediateStep(double rate, int frames)
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var timing = new MovementTiming { FrameRate = rate, FramesPerStep = frames };
        var player = new MapObject("player", Vector2D.Zero, receiver) { MovementInterval = timing.Interval };
        var bullet = new MapObject("bullet", Vector2D.Zero, receiver, kind: MapObjectKind.Projectile)
        { MovementInterval = timing.Interval };
        foreach (var body in new[] { player, bullet })
            context.Dispatch(MapMovementEvent.Schedule(context, receiver, body, new(40, 0), 40)).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(0.1));
        Assert.AreEqual(4, player.Position.X, 0.00002);
        Assert.AreEqual(player.Position, bullet.Position);
        context.RunUntilQueueEmpty();
        var steps = context.History.OfType<MapMovementUpdatedEvent>().ToArray();
        var playerSteps = steps.Where(ev => ev.Movement.Body == player).ToArray();
        var bulletSteps = steps.Where(ev => ev.Movement.Body == bullet).ToArray();
        CollectionAssert.AreEqual(playerSteps.Select(ev => (ev.At, ev.Position)).ToArray(),
            bulletSteps.Select(ev => (ev.At, ev.Position)).ToArray());
        Assert.IsGreaterThan(2, playerSteps.Length);
        Assert.AreEqual(TimeSpan.FromSeconds(1), playerSteps[^1].At);
        Assert.AreEqual(new Vector2D(40, 0), player.Position);
        TestContext.WriteLine($"{rate}Hz/{frames}: {playerSteps.Length} steps, final={player.Position}, at={playerSteps[^1].At}");
    }

    [TestMethod]
    public void BoundaryChecksEntireSegmentAndConnectedUnion()
    {
        var rooms = new[]
        {
            new MapRoomDefinition("a", "a", "", 0, 0, 10, 10),
            new MapRoomDefinition("b", "b", "", 20, 0, 10, 10)
        };
        var isolated = new MapDefinition("map", "map", 40, 20, "Interior", rooms: rooms);
        var blocked = isolated.ClipMovement(new(5, 5), new(25, 5));
        Assert.IsTrue(blocked.Blocked);
        Assert.AreEqual(new Vector2D(10, 5), blocked.Position);
        var connected = new MapDefinition("map", "map", 40, 20, "Interior", rooms: rooms,
            corridors: [new MapCorridorDefinition("c", "a", "b", 10, 5, 20, 5, 2)]);
        Assert.IsFalse(connected.ClipMovement(new(5, 5), new(25, 5)).Blocked);
        Assert.IsTrue(connected.ClipMovement(new(5, 9), new(25, 9)).Blocked);
        Assert.AreEqual(new Vector2D(10, 9), connected.ClipMovement(new(10, 9), new(15, 9)).Position);
        Assert.IsFalse(connected.ClipMovement(new(10, 9), new(5, 9)).Blocked);
        TestContext.WriteLine($"First blocked point={blocked.Position}; connected corridor remains open.");
    }

    [TestMethod]
    public void HomingTracksThenFreezesLostTargetAndMisses()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var target = new MapObject("target", new(10, 0), receiver);
        var bullet = new MapObject("bullet", Vector2D.Zero, receiver, kind: MapObjectKind.Projectile);
        context.Dispatch(MapMovementEvent.Schedule(context, receiver, bullet, target.Position, 10,
            mode: MovementMode.Homing, trackingTarget: target)).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(0.2));
        target.Position = new(10, 4);
        context.AdvanceTo(TimeSpan.FromSeconds(0.4));
        Assert.IsGreaterThan(0, bullet.Position.Y);
        target.IsActive = false;
        target.Position = new(100, 100);
        context.RunUntilQueueEmpty();
        Assert.AreEqual(new Vector2D(10, 4), bullet.Position);
        Assert.AreEqual(MovementEndReason.Missed, bullet.Movement!.EndReason);
        TestContext.WriteLine($"Lost target arrival={bullet.Position}, outcome={bullet.Movement.EndReason}");
    }

    [TestMethod]
    public void InvalidTimingAndHomingTargetAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new MovementTiming { FrameRate = 0 }.Interval);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new MovementTiming { FramesPerStep = 0 }.Interval);
        var body = new MapObject("body", Vector2D.Zero, new EventReceiver());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new MovementState(body, new(1, 0), 10, TimeSpan.Zero, mode: MovementMode.Homing));
    }

    [TestMethod]
    public void LogicalSpaceMatchesMovementForWrappedInteriorAndIndependentMaps()
    {
        var world = new MapRuntime(new MapDefinition("world", "world", 100, 100));
        var interior = new MapRuntime(new MapDefinition("inside", "inside", 100, 100,
            "Interior", "Torus"));
        var bounded = world.CreateTemporaryMap(new(50, 50), isMainPlane: false);
        Vector2D from = new(1, 50), to = new(99, 50);

        Assert.AreEqual(2, world.Space.Distance(from, to), 0.000001);
        Assert.AreEqual(world.Movement.Distance(from, to), world.Space.Distance(from, to), 0.000001);
        Assert.AreEqual(new Vector2D(-2, 0), world.Space.Delta(from, to));
        Assert.AreEqual(new Vector2D(0, 50), world.Space.MoveTowards(from, to, 1));
        Assert.AreEqual(2, world.Space.Distance(new(201, 50), new(-1, 50)), 0.000001);
        Assert.AreEqual(98, interior.Space.Distance(from, to), 0.000001);
        Assert.AreEqual(68, bounded.Space.Distance(new(16, 50), new(84, 50)), 0.000001);
        Assert.IsFalse(bounded.Space.IsWrapped);
        TestContext.WriteLine("Wrapped map distance follows the short seam; interiors and independent bounds remain planar.");
    }
}
