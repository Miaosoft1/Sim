using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class MapRuntimeStateTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DirectoryTracksParallelRootsAndExistingOrNewSubmaps()
    {
        var (root, manager, body) = Create();
        var existing = new MapRuntime();
        root.RegisterDungeonMap("existing", existing);
        var directory = new MapInstanceDirectory();
        directory.Register(root);
        var parallel = new MapRuntime();
        directory.Register(parallel);
        Assert.AreSame(root, existing.Root);
        Assert.AreSame(parallel, parallel.Root);
        Assert.HasCount(3, directory.Maps);

        var added = new MapRuntime();
        existing.RegisterDungeonMap("added", added);
        var temporary = added.CreateTemporaryMap(new(20, 20), isMainPlane: false);
        Assert.AreSame(root, added.Root);
        Assert.AreSame(root, temporary.Root);
        Assert.AreSame(directory, temporary.Directory);
        Assert.HasCount(5, directory.Maps);
        int notifications = 0;
        directory.VisibilityChanged += (_, _) => notifications++;
        var marker = new MapObject("marker", new(20, 20), body.EventReceiver);
        temporary.AddObject(marker);
        Assert.IsGreaterThan(0, notifications);
        Assert.ThrowsExactly<InvalidOperationException>(() => directory.Unregister(added));
        Assert.ThrowsExactly<InvalidOperationException>(() => directory.Unregister(root));
        temporary.RemoveObject(marker);
        temporary.Close();
        Assert.IsNull(temporary.Directory);
        Assert.HasCount(4, directory.Maps);
        manager.Clear();
        Assert.IsTrue(directory.Unregister(root));
        Assert.IsNull(existing.Directory);
        Assert.IsNull(added.Directory);
        Assert.HasCount(1, directory.Maps);
        Assert.AreSame(parallel, directory.Maps.Values.Single());
        TestContext.WriteLine("The directory tracked both parallel roots and nested submaps, including temporary-map cleanup.");
    }

    private static (MapRuntime Root, UnitMapObjectManager Manager, MapObject Body) Create()
    {
        var context = new Context();
        var body = new MapObject("unit", new(20, 20), context.Factory.NewEventReceiver());
        var manager = new UnitMapObjectManager(body.Id);
        manager.Initialize(body);
        var root = new MapRuntime();
        root.AddObject(body);
        return (root, manager, body);
    }

    [TestMethod]
    public void DormantRegistrationDoesNotTakeControlAndClearRemovesEveryObject()
    {
        var (root, manager, body) = Create();
        var interior = new MapRuntime();
        root.RegisterDungeonMap("dungeon", interior);
        var dormant = new MapObject(body.Id, new(5, 5), body.EventReceiver);
        long version = manager.ControlVersion;
        manager.Register(interior, dormant);
        Assert.AreSame(body, manager.ControlledObject);
        Assert.AreEqual(version, manager.ControlVersion);
        Assert.IsTrue(dormant.IsInteractable);
        Assert.IsFalse(dormant.CanMove);
        Assert.IsFalse(dormant.CanCast);
        Assert.AreSame(dormant, manager.Find(interior));
        manager.Deactivate();
        Assert.IsTrue(manager.Objects.Values.All(item => !item.IsActive && !item.CanMove && !item.CanCast));
        manager.Clear();
        Assert.IsEmpty(root.Objects);
        Assert.IsEmpty(interior.Objects);
        Assert.IsEmpty(manager.Objects);
        Assert.IsNull(manager.ControlledObject);
    }

    [TestMethod]
    public void ReactivationRestoresEachRepresentationAndChangesMadeWhileInactive()
    {
        var (root, manager, body) = Create();
        var interior = new MapRuntime();
        root.RegisterDungeonMap("dungeon", interior);
        var dormant = new MapObject(body.Id, new(5, 5), body.EventReceiver);
        manager.Register(interior, dormant);
        manager.SetState(root, canCast: false);

        manager.Deactivate();
        manager.Deactivate();
        manager.SetState(root, canMove: false, canCast: true);
        Assert.IsFalse(body.IsActive);
        Assert.IsFalse(body.CanMove);
        Assert.IsFalse(body.CanCast);
        Assert.IsFalse(dormant.CanMove);
        Assert.IsFalse(dormant.CanCast);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Register(new MapRuntime(), new MapObject(body.Id, new(5, 5), body.EventReceiver)));

        manager.Reactivate();
        Assert.IsTrue(body.IsActive);
        Assert.IsFalse(body.CanMove);
        Assert.IsTrue(body.CanCast);
        Assert.IsTrue(dormant.IsActive);
        Assert.IsFalse(dormant.CanMove);
        Assert.IsFalse(dormant.CanCast);
        manager.Reactivate();
        Assert.IsFalse(body.CanMove);
        Assert.IsTrue(body.CanCast);
        TestContext.WriteLine("Repeated deactivation retained per-representation permissions and deferred inactive changes until revival.");
    }

    [TestMethod]
    public void ReturningWhileInactiveRestoresOnlyTheReturnedRepresentationPermissions()
    {
        var (root, manager, body) = Create();
        var interior = new MapRuntime();
        root.RegisterDungeonMap("dungeon", interior);
        manager.SetState(root, canCast: false);
        MapObject inside = manager.Enter(interior, new(10, 10), TimeSpan.Zero, false, "dungeon");
        manager.Deactivate();
        Assert.AreSame(body, manager.Return(TimeSpan.Zero));
        Assert.IsFalse(body.IsActive);
        Assert.IsFalse(body.CanMove);
        manager.Reactivate();
        Assert.IsTrue(body.IsActive);
        Assert.IsTrue(body.CanMove);
        Assert.IsFalse(body.CanCast);
        Assert.IsFalse(inside.CanMove);
        Assert.IsFalse(inside.CanCast);
        TestContext.WriteLine("Returning while inactive restored the destination's original permissions, not the abandoned representation's control.");
    }

    [TestMethod]
    public void ReturnRestoresExactPermissionsAndReentryReusesRetainedRepresentation()
    {
        var (root, manager, body) = Create();
        var interior = new MapRuntime();
        root.RegisterDungeonMap("dungeon", interior);
        manager.SetState(root, isSelectable: false, canCast: false);
        var inside = manager.Enter(interior, new(10, 10), TimeSpan.Zero, false, "dungeon");
        Assert.IsFalse(body.IsVisible);
        Assert.IsTrue(body.IsInteractable);
        Assert.AreSame(body, manager.Return(TimeSpan.Zero));
        Assert.IsTrue(body.IsVisible);
        Assert.IsFalse(body.IsSelectable);
        Assert.IsFalse(body.CanCast);
        Assert.IsTrue(body.CanMove);
        Assert.IsTrue(inside.IsInteractable);
        Assert.IsFalse(inside.IsVisible);
        Assert.AreSame(inside, manager.Enter(interior, new(12, 12), TimeSpan.Zero, false, "dungeon"));
        Assert.HasCount(2, manager.Objects);
    }

    [TestMethod]
    public void RemovingNonControlledReturnTargetDoesNotLoseRemainingControlPath()
    {
        var (root, manager, body) = Create();
        var interior = new MapRuntime();
        root.RegisterDungeonMap("dungeon", interior);
        var inside = manager.Enter(interior, new(10, 10), TimeSpan.Zero, false, "dungeon");
        var spirit = interior.CreateTemporaryMap(inside.Position, isMainPlane: false);
        var soul = manager.Enter(spirit, inside.Position, TimeSpan.Zero, true, appearanceOverlay: true);
        Assert.AreSame(root, spirit.Root);
        Assert.HasCount(2, root.SubMaps);
        root.RemoveObject(body);
        Assert.AreSame(soul, manager.ControlledObject);
        Assert.ThrowsExactly<InvalidOperationException>(() => spirit.Close());
        Assert.AreSame(soul, manager.ControlledObject);
        Assert.IsFalse(spirit.IsClosing);
        manager.Return(TimeSpan.Zero);
        spirit.Close();
        Assert.AreSame(inside, manager.ControlledObject);
        Assert.IsTrue(inside.CanMove);
        Assert.IsFalse(manager.CanReturn);
        Assert.HasCount(1, manager.Objects);
        Assert.HasCount(1, root.SubMaps);
    }

    [TestMethod]
    public void ClosingSourceWithNestedControlPathIsRejectedWithoutMutation()
    {
        var (root, manager, body) = Create();
        var source = root.CreateTemporaryMap(body.Position, isMainPlane: false);
        var sourceBody = manager.Enter(source, body.Position, TimeSpan.Zero, true);
        var child = source.CreateTemporaryMap(sourceBody.Position, isMainPlane: false);
        var childBody = manager.Enter(child, sourceBody.Position, TimeSpan.Zero, true);
        long version = manager.ControlVersion;

        Assert.ThrowsExactly<InvalidOperationException>(() => source.Close());
        Assert.ThrowsExactly<InvalidOperationException>(() => child.Close());
        Assert.IsFalse(source.IsClosing);
        Assert.IsFalse(child.IsClosing);
        Assert.AreSame(childBody, manager.ControlledObject);
        Assert.AreEqual(version, manager.ControlVersion);
        Assert.HasCount(3, manager.Objects);
        Assert.HasCount(2, root.SubMaps);

        manager.Return(TimeSpan.Zero);
        child.Close();
        manager.Return(TimeSpan.Zero);
        source.Close();
        Assert.AreSame(body, manager.ControlledObject);
        Assert.IsEmpty(root.SubMaps);
        TestContext.WriteLine("Closing a nested active path was rejected before any state changed.");
    }

    [TestMethod]
    public void ConfiguredPoliciesResolveNestedPathsBeforeClosingTheirMaps()
    {
        var (root, manager, body) = Create();
        var source = root.CreateTemporaryMap(body.Position, isMainPlane: false);
        var sourceBody = manager.Enter(source, body.Position, TimeSpan.Zero, true);
        var child = source.CreateTemporaryMap(sourceBody.Position, isMainPlane: false);
        var childBody = manager.Enter(child, sourceBody.Position, TimeSpan.Zero, true);
        var childPolicy = new ReturnClosingPolicy(manager, "child-return");
        var sourcePolicy = new ReturnClosingPolicy(manager, "source-return", child);
        child.ConfigureClosingPolicy(childPolicy);
        source.ConfigureClosingPolicy(sourcePolicy);

        Assert.ThrowsExactly<InvalidOperationException>(() => source.Close());
        Assert.ThrowsExactly<InvalidOperationException>(() => child.Close());
        Assert.ThrowsExactly<InvalidOperationException>(() => child.RemoveObject(childBody));
        Assert.AreSame(childBody, manager.ControlledObject);
        Assert.IsFalse(source.IsClosing);
        Assert.IsFalse(child.IsClosing);

        source.Close(new MapCloseRequest("source-return", TimeSpan.Zero));

        Assert.IsTrue(source.IsClosing);
        Assert.IsTrue(child.IsClosing);
        Assert.AreSame(body, manager.ControlledObject);
        Assert.IsEmpty(root.SubMaps);
        Assert.AreEqual(1, sourcePolicy.Executions);
        Assert.AreEqual(1, childPolicy.Executions);
        TestContext.WriteLine("The source policy validated both paths before returning through and closing the child and source.");
    }

    [TestMethod]
    public void ConfiguredPolicyRejectsUnplannedControlPathBeforeAnyMutation()
    {
        var (root, manager, body) = Create();
        var temporary = root.CreateTemporaryMap(body.Position, isMainPlane: false);
        var controlled = manager.Enter(temporary, body.Position, TimeSpan.Zero, true);
        var other = new MapObject("other", body.Position, body.EventReceiver);
        var otherManager = new UnitMapObjectManager(other.Id);
        otherManager.Initialize(other);
        temporary.AddObject(other);
        var policy = new ReturnClosingPolicy(manager, "return");
        temporary.ConfigureClosingPolicy(policy);
        long version = manager.ControlVersion;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            temporary.Close(new MapCloseRequest("return", TimeSpan.Zero)));
        Assert.IsFalse(temporary.IsClosing);
        Assert.AreEqual(0, policy.Executions);
        Assert.AreSame(controlled, manager.ControlledObject);
        Assert.AreSame(other, otherManager.ControlledObject);
        Assert.AreEqual(version, manager.ControlVersion);
        Assert.AreSame(temporary, controlled.MapRuntime);
        Assert.AreSame(temporary, other.MapRuntime);

        otherManager.Clear();
        temporary.Close(new MapCloseRequest("return", TimeSpan.Zero));
        Assert.AreSame(body, manager.ControlledObject);
        TestContext.WriteLine("An unplanned active control path rejected closure before either participant was removed.");
    }

    private sealed class ReturnClosingPolicy(
        UnitMapObjectManager manager, string reason, MapRuntime? child = null) : IMapClosingPolicy
    {
        public int Executions { get; private set; }

        public void Validate(MapRuntime map, MapCloseRequest request)
        {
            if (request.Reason != reason || manager.Find(map) is not { } body || !manager.CanReturn)
                throw new InvalidOperationException("The configured return path is unavailable.");
            if (child != null)
            {
                child.ValidateClose(new MapCloseRequest("child-return", request.Now));
                map.ValidateCloseAfterRemoving([body], [child]);
            }
            else
            {
                if (manager.ControlledObject != body)
                    throw new InvalidOperationException("The return object is not controlled.");
                map.ValidateCloseAfterRemoving([body]);
            }
        }

        public void Execute(MapRuntime map, MapCloseRequest request)
        {
            Executions++;
            child?.Close(new MapCloseRequest("child-return", request.Now));
            manager.Return(request.Now);
        }
    }

    [TestMethod]
    public void RejectedTransitionLeavesControlStateUnchanged()
    {
        var (root, manager, body) = Create();
        var closed = root.CreateTemporaryMap(body.Position, isMainPlane: false);
        closed.Close();
        long version = manager.ControlVersion;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.Enter(closed, body.Position, TimeSpan.Zero, true, appearanceOverlay: true));
        Assert.AreSame(body, manager.ControlledObject);
        Assert.AreEqual(version, manager.ControlVersion);
        Assert.HasCount(1, manager.Objects);
        Assert.IsTrue(body.IsVisible && body.IsSelectable && body.CanMove && body.CanCast);
    }

    [TestMethod]
    public void CrossingCorridorsHaveATurningPathWithoutTeleportingAcrossVoid()
    {
        var map = new MapDefinition("cross", "Cross", 100, 100, "Interior",
            rooms: [
                new("left", "Left", "Room", 0, 40, 10, 20),
                new("right", "Right", "Room", 90, 40, 10, 20),
                new("top", "Top", "Room", 40, 0, 20, 10),
                new("bottom", "Bottom", "Room", 40, 90, 20, 10)
            ],
            corridors: [
                new("horizontal", "left", "right", 10, 50, 90, 50, 4),
                new("vertical", "top", "bottom", 50, 10, 50, 90, 4)
            ]);
        Vector2D start = new(5, 50), destination = new(50, 5);
        Assert.IsTrue(map.ClipMovement(start, destination).Blocked);
        var path = map.FindPath(start, destination);
        Assert.IsNotNull(path);
        double length = 0;
        foreach (var waypoint in path)
        {
            Assert.IsFalse(map.ClipMovement(start, waypoint).Blocked);
            length += start.DistanceTo(waypoint);
            start = waypoint;
        }
        Assert.AreEqual(destination, start);
        Assert.IsLessThan(90d, length);
        TestContext.WriteLine($"Crossing-corridor route: {path.Count} segments, length {length:F4}.");
    }

    [TestMethod]
    public void SpiritBoundsIgnoreOriginalMapAndClipTheEntireSegment()
    {
        var root = new MapRuntime(10, 10);
        var spirit = root.CreateTemporaryMap(new(1, 1), isMainPlane: false);
        Assert.IsInstanceOfType<BoundedMovement>(spirit.Movement);
        var clipped = spirit.Movement.Clip(new(1, 1), new(-100, 1));
        Assert.IsTrue(clipped.Blocked);
        Assert.AreEqual(-33d, clipped.Position.X, 1e-8);
        Assert.IsNotNull(spirit.Movement.FindPath(new(1, 1), new(-20, -20)));
        Assert.IsTrue(spirit.Contains(new(-20, -20)));
        TestContext.WriteLine($"Spirit edge: {clipped.Position}; base width: {root.Width}.");
    }

    [TestMethod]
    public void PlaneClassificationIsIndependentOfGeometryAndLifetime()
    {
        var interiorMain = new MapRuntime(new MapDefinition("sect", "Sect", 100, 100,
            "Interior", isMainPlane: true));
        var outdoorInstance = new MapRuntime(new MapDefinition("instance", "Instance", 100, 100,
            "Overworld", isMainPlane: false));
        var temporaryMain = interiorMain.CreateTemporaryMap(new(50, 50), isMainPlane: true);
        var temporaryInstance = interiorMain.CreateTemporaryMap(new(50, 50), isMainPlane: false);

        Assert.IsTrue(interiorMain.IsMainPlane);
        Assert.IsFalse(outdoorInstance.IsMainPlane);
        Assert.IsTrue(temporaryMain.IsMainPlane);
        Assert.IsFalse(temporaryInstance.IsMainPlane);
        TestContext.WriteLine("Plane classification follows explicit configuration, not geometry or lifetime.");
    }

    [TestMethod]
    [DataRow(0, -23.5, 20.0)]
    [DataRow(1, 43.5, 20.0)]
    [DataRow(2, 10.0, -13.5)]
    [DataRow(3, 10.0, 53.5)]
    public void InsetEdgeMidpointKeepsTheWholeBodyInside(int edge, double x, double y)
    {
        var bounds = new BoundedMovement(new(10, 20), 68);
        Vector2D position = bounds.GetInsetEdgeMidpoint(edge, 0.5);
        Assert.AreEqual(new Vector2D(x, y), position);
        Assert.IsTrue(bounds.Contains(new(position.X - 0.5, position.Y - 0.5)));
        Assert.IsTrue(bounds.Contains(new(position.X + 0.5, position.Y + 0.5)));
        TestContext.WriteLine($"Edge {edge}: {position}, radius 0.5.");
    }

    [TestMethod]
    public void InsetEdgeMidpointRejectsInvalidEdgesAndRadii()
    {
        var bounds = new BoundedMovement(new(10, 20), 68);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bounds.GetInsetEdgeMidpoint(4, 0.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bounds.GetInsetEdgeMidpoint(0, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bounds.GetInsetEdgeMidpoint(0, 34.1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bounds.GetInsetEdgeMidpoint(0, double.NaN));
    }
}
