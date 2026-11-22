using Miaosoft.Sim.Combat.Map.Hex;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class HexBoardRuntimeTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AxialCoordinatesHaveSixUnitDistanceNeighbors()
    {
        HexCoordinate origin = new(0, 0);
        HexCoordinate[] neighbors = origin.GetNeighbors().ToArray();

        Assert.HasCount(6, neighbors);
        Assert.AreEqual(6, neighbors.Distinct().Count());
        Assert.IsTrue(neighbors.All(cell => origin.DistanceTo(cell) == 1));
        Assert.AreEqual(2, origin.DistanceTo(new HexCoordinate(1, 1)));
        TestContext.WriteLine("Axial distance and all six neighboring cells use hex-grid steps.");
    }

    [TestMethod]
    public void WorldProjectionRoundTripsForBothOrientations()
    {
        HexCoordinate[] coordinates =
        [
            new(-8, 3),
            new(-2, -4),
            new(0, 0),
            new(4, -1),
            new(5, 6),
        ];

        foreach (HexOrientation orientation in Enum.GetValues<HexOrientation>())
        {
            foreach (HexCoordinate coordinate in coordinates)
            {
                Vector2D world = coordinate.ToWorld(12.5, orientation);
                Assert.AreEqual(coordinate, HexCoordinate.FromWorld(world, 12.5, orientation));
            }
        }

        TestContext.WriteLine("Pointy-top and flat-top hex projections round-trip across positive and negative axial coordinates.");
    }

    [TestMethod]
    public void LayoutContainsPlayableCellsAndHandlesCoordinateLimits()
    {
        var definition = new HexBoardDefinition([new(-2, 1), new(0, 0), new(1, -1)]);
        foreach (HexOrientation orientation in Enum.GetValues<HexOrientation>())
        {
            var layout = new HexBoardLayout(definition, 12, orientation);
            foreach (HexCoordinate cell in definition.PlayableCells)
            {
                Vector2D center = layout.ToWorld(cell);
                Assert.IsTrue(layout.Contains(center));
                Assert.AreEqual(cell, layout.FromWorld(center));
            }
            Assert.IsFalse(layout.Contains(new Vector2D(double.MaxValue, double.MaxValue)));
            Assert.AreEqual(2, layout.Distance(layout.ToWorld(new(-2, 1)), layout.ToWorld(new(0, 0))));
        }
        HexCoordinate[] boundaryNeighbors = new HexCoordinate(int.MaxValue, int.MaxValue).GetNeighbors().ToArray();
        Assert.HasCount(2, boundaryNeighbors);
        Assert.IsTrue(boundaryNeighbors.All(cell => cell.Q >= int.MaxValue - 1 && cell.R >= int.MaxValue - 1));
        TestContext.WriteLine("Both board projections fit positive map bounds and reject coordinates without integer wraparound.");
    }

    [TestMethod]
    public void ReservationKeepsOriginUntilAtomicMoveCommit()
    {
        HexBoardRuntime board = CreateBoard();
        Guid unitId = Guid.NewGuid();
        HexCoordinate origin = new(0, 0);
        HexCoordinate destination = new(1, 0);

        Assert.IsTrue(board.TryPlace(unitId, origin));
        Assert.IsTrue(board.TryReserveMove(unitId, destination));
        Assert.AreEqual(unitId, board.Occupants[origin]);
        Assert.IsTrue(board.Reservations.ContainsKey(destination));
        Assert.IsFalse(board.TryPlace(Guid.NewGuid(), destination));
        Assert.IsFalse(board.TryReserveMove(Guid.NewGuid(), destination));

        Assert.IsTrue(board.CommitMove(unitId));
        Assert.IsFalse(board.Occupants.ContainsKey(origin));
        Assert.AreEqual(unitId, board.Occupants[destination]);
        Assert.IsFalse(board.Reservations.ContainsKey(destination));
        TestContext.WriteLine("A move reserves its destination while preserving origin occupancy until commit.");
    }

    [TestMethod]
    public void CancelAndRemoveReleaseOwnedReservations()
    {
        HexBoardRuntime board = CreateBoard();
        Guid unitId = Guid.NewGuid();
        HexCoordinate origin = new(0, 0);
        HexCoordinate destination = new(0, 1);

        Assert.IsTrue(board.TryPlace(unitId, origin));
        Assert.IsTrue(board.TryReserveMove(unitId, destination));
        Assert.IsTrue(board.CancelMove(unitId));
        Assert.IsFalse(board.Reservations.ContainsKey(destination));
        Assert.IsFalse(board.CommitMove(unitId));

        Assert.IsTrue(board.TryReserveMove(unitId, destination));
        Assert.IsTrue(board.Remove(unitId));
        Assert.IsFalse(board.Occupants.ContainsKey(origin));
        Assert.IsFalse(board.Positions.ContainsKey(unitId));
        Assert.IsFalse(board.Reservations.ContainsKey(destination));
        TestContext.WriteLine("Cancellation and removal leave no owned occupancy or move reservation.");
    }

    [TestMethod]
    public void TransferFailureLeavesBothBoardsUnchangedAndSuccessKeepsUnitId()
    {
        HexBoardRuntime source = CreateBoard();
        HexBoardRuntime destination = CreateBoard();
        Guid unitId = Guid.NewGuid();
        Guid blockerId = Guid.NewGuid();
        HexCoordinate sourceCell = new(0, 0);
        HexCoordinate occupiedCell = new(1, 0);
        HexCoordinate openCell = new(-1, 0);

        Assert.IsTrue(source.TryPlace(unitId, sourceCell));
        Assert.IsTrue(destination.TryPlace(blockerId, occupiedCell));

        Assert.IsFalse(source.TryTransferTo(destination, unitId, occupiedCell));
        Assert.AreEqual(unitId, source.Occupants[sourceCell]);
        Assert.IsFalse(destination.Positions.ContainsKey(unitId));

        Assert.IsTrue(source.TryTransferTo(destination, unitId, openCell));
        Assert.IsFalse(source.Positions.ContainsKey(unitId));
        Assert.AreEqual(unitId, destination.Occupants[openCell]);
        Assert.AreEqual(blockerId, destination.Occupants[occupiedCell]);
        Assert.IsFalse(source.TryTransferTo(destination, unitId, new HexCoordinate(0, 1)));
        TestContext.WriteLine("Failed transfers are atomic; successful transfers preserve the same unit identifier.");
    }

    [TestMethod]
    public void PreparationRepositionIsAtomicAndDoesNotBypassReservations()
    {
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0), new(2, 0), new(3, 0)]);
        var board = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "reposition", "Reposition", 1);
        using var context = new Context();
        MapObject first = CreateBody(context);
        MapObject blocker = CreateBody(context);
        Guid firstId = Guid.Parse(first.Id);
        Assert.IsTrue(board.TryPlace(first, new(0, 0)));
        Assert.IsTrue(board.TryPlace(blocker, new(3, 0)));
        Assert.IsTrue(board.TryReserve(first, board.Layout.ToWorld(new(1, 0))));
        Assert.IsFalse(board.TryReposition(first, new(2, 0)));
        Assert.AreEqual(new HexCoordinate(0, 0), board.Positions[firstId]);
        Assert.AreEqual(firstId, board.Reservations[new(1, 0)]);
        board.Cancel(first);
        Assert.IsFalse(board.TryReposition(first, new(3, 0)));
        Assert.IsFalse(board.TryReposition(first, new(9, 0)));
        Assert.AreEqual(board.Layout.ToWorld(new(0, 0)), first.Position);

        bool observedCommitted = false;
        first.Manager!.SpatialChanged += _ =>
            observedCommitted = board.Positions[firstId] == new HexCoordinate(2, 0) &&
                first.Position == board.Layout.ToWorld(new(2, 0));
        Assert.IsTrue(board.TryReposition(first, new(2, 0)));
        Assert.IsTrue(observedCommitted);
        Assert.IsFalse(board.Occupants.ContainsKey(new(0, 0)));
        Assert.AreEqual(firstId, board.Occupants[new(2, 0)]);
        Assert.IsTrue(board.TryReposition(first, new(2, 0)));
        TestContext.WriteLine("Preparation reposition rejected occupied, invalid and reserved moves and published committed occupancy with its position.");
    }

    [TestMethod]
    public void FirstPlacementNotifiesOnlyAfterMapAndOccupancyCommit()
    {
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0)]);
        var board = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "placement", "Placement", 1);
        using var context = new Context();
        MapObject unit = CreateBody(context);
        Guid unitId = Guid.Parse(unit.Id);
        int notifications = 0;
        unit.Manager!.SpatialChanged += _ =>
        {
            notifications++;
            Assert.AreSame(board.Map, unit.MapRuntime);
            Assert.AreEqual(new HexCoordinate(1, 0), board.Positions[unitId]);
            Assert.AreEqual(board.Layout.ToWorld(new(1, 0)), unit.Position);
        };

        Assert.IsTrue(board.TryPlace(unit, new(1, 0)));
        Assert.AreEqual(1, notifications);
        TestContext.WriteLine("Initial placement notified observers only after the unit joined its occupied cell.");
    }

    [TestMethod]
    public void RepositionNotificationFailureDoesNotRollBackOnlyOccupancy()
    {
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0)]);
        var board = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "notification", "Notification", 1);
        using var context = new Context();
        MapObject unit = CreateBody(context);
        Guid unitId = Guid.Parse(unit.Id);
        Assert.IsTrue(board.TryPlace(unit, new(0, 0)));
        unit.Manager!.SpatialChanged += _ => throw new InvalidOperationException("expected reposition observer failure");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => board.TryReposition(unit, new(1, 0)));
        StringAssert.Contains(error.ToString(), "expected reposition observer failure");
        Assert.AreEqual(new HexCoordinate(1, 0), board.Positions[unitId]);
        Assert.AreEqual(board.Layout.ToWorld(new(1, 0)), unit.Position);
        Assert.AreEqual(unitId, board.Occupants[new(1, 0)]);
        Assert.IsFalse(board.Occupants.ContainsKey(new(0, 0)));
        TestContext.WriteLine("A post-commit observer error was surfaced without splitting occupancy from position.");
    }

    [TestMethod]
    public void BoardDefinitionRejectsEmptyOrDuplicateCells()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new HexBoardDefinition([]));
        Assert.ThrowsExactly<ArgumentException>(() => new HexBoardDefinition(
            [new HexCoordinate(0, 0), new HexCoordinate(0, 0)]));
        TestContext.WriteLine("Board definitions reject empty and duplicate playable-cell sets.");
    }

    [TestMethod]
    public void BoardMapRoutesAroundOccupiedCellsAndProjectilesDoNotOccupyCells()
    {
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0), new(2, 0), new(1, -1), new(2, -1)]);
        var board = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "board", "Board", 1);
        var context = new Context();
        MapObject first = CreateBody(context);
        MapObject blocker = CreateBody(context);
        Assert.IsTrue(board.TryPlace(first, new(0, 0)));
        Assert.IsTrue(board.TryPlace(blocker, new(1, 0)));
        Assert.IsTrue(board.TryFindPath(first, board.Layout.ToWorld(new(2, 0)), out var path));
        CollectionAssert.AreEqual(new[] { new HexCoordinate(1, -1), new HexCoordinate(2, -1), new HexCoordinate(2, 0) },
            path.Select(board.Layout.FromWorld).ToArray());
        Assert.IsTrue(board.TryReserve(first, path[0]));
        Assert.AreEqual(new HexCoordinate(0, 0), board.Positions[Guid.Parse(first.Id)]);
        Assert.IsTrue(board.TryCommit(first));
        Assert.AreEqual(new HexCoordinate(1, -1), board.Positions[Guid.Parse(first.Id)]);
        Assert.AreEqual(path[0], first.Position);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Position = board.Layout.ToWorld(new(2, 0)));

        var projectile = new MapObject("projectile", first.Position, context.Factory.NewEventReceiver(),
            kind: MapObjectKind.Projectile);
        board.Map.AddObject(projectile);
        Assert.HasCount(2, board.Occupants);
        projectile.Position = board.Layout.ToWorld(new(1, 0));
        Assert.HasCount(2, board.Occupants);
        TestContext.WriteLine("Pathfinding avoided occupied cells, movement committed atomically, and a projectile crossed occupied cells without reserving them.");
    }

    [TestMethod]
    public void AttackRangePathUsesReachableUnoccupiedCellsAndRejectsOtherBoards()
    {
        var definition = new HexBoardDefinition(
            [new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(1, -1), new(2, -1)]);
        var board = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "attack", "Attack", 1);
        var other = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "other", "Other", 1);
        using var context = new Context();
        MapObject attacker = CreateBody(context);
        MapObject target = CreateBody(context);
        MapObject blocker = CreateBody(context);
        MapObject remote = CreateBody(context);
        Assert.IsTrue(board.TryPlace(attacker, new(0, 0)));
        Assert.IsTrue(board.TryPlace(target, new(3, 0)));
        Assert.IsTrue(board.TryPlace(blocker, new(1, 0)));
        Assert.IsTrue(other.TryPlace(remote, new(3, 0)));

        Assert.IsTrue(board.TryFindPathToRange(attacker, target, 1, out var path));
        CollectionAssert.AreEqual(new[] { new HexCoordinate(1, -1), new HexCoordinate(2, -1), new HexCoordinate(2, 0) },
            path.Select(board.Layout.FromWorld).ToArray());
        Assert.AreEqual(1L, board.Layout.FromWorld(path[^1]).DistanceTo(new(3, 0)));
        Assert.IsFalse(board.TryFindPathToRange(attacker, target, 0, out _));
        Assert.IsFalse(board.TryFindPathToRange(attacker, remote, 1, out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => board.TryFindPathToRange(attacker, target, double.NaN, out _));

        Assert.IsTrue(board.TryReserve(attacker, board.Layout.ToWorld(new(1, -1))));
        Assert.IsTrue(board.TryFindPathToRange(attacker, target, 1, out var reservedPath));
        CollectionAssert.AreEqual(path.ToArray(), reservedPath.ToArray());
        Assert.IsTrue(board.TryReserve(blocker, board.Layout.ToWorld(new(2, -1))));
        Assert.IsFalse(board.TryFindPathToRange(attacker, target, 1, out _));
        board.Cancel(blocker);
        board.Cancel(attacker);
        Assert.IsTrue(board.TryFindPathToRange(attacker, target, 3, out var alreadyInRange));
        Assert.IsEmpty(alreadyInRange);
        Assert.HasCount(3, board.Occupants);
        TestContext.WriteLine("Approach paths ended on the nearest reachable free attack cell, respected reservations, and never crossed boards.");
    }

    [TestMethod]
    public void ParallelBoardTransferPreservesUnitAndRollsBackRejectedMoves()
    {
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0)]);
        var source = new HexBoardMap(definition, 10, HexOrientation.FlatTop, "source", "Source", 1);
        var destination = new HexBoardMap(definition, 10, HexOrientation.FlatTop, "destination", "Destination", 1);
        var directory = new MapInstanceDirectory();
        directory.Register(source.Map);
        directory.Register(destination.Map);
        Assert.AreSame(source.Map, source.Map.Root);
        Assert.AreSame(destination.Map, destination.Map.Root);
        var context = new Context();
        MapObject body = CreateBody(context);
        MapObject blocker = CreateBody(context);
        UnitMapObjectManager manager = body.Manager!;
        Assert.IsTrue(source.TryPlace(body, new(0, 0)));
        Assert.IsTrue(destination.TryPlace(blocker, new(1, 0)));
        long version = manager.ControlVersion;

        Assert.IsFalse(source.TryTransferTo(destination, body, new(1, 0), TimeSpan.Zero));
        Assert.AreSame(body, manager.ControlledObject);
        Assert.AreEqual(new HexCoordinate(0, 0), source.Positions[Guid.Parse(body.Id)]);
        Assert.AreEqual(version, manager.ControlVersion);
        Assert.IsTrue(source.TryReserve(body, source.Layout.ToWorld(new(1, 0))));
        Assert.IsFalse(source.TryTransferTo(destination, body, new(1, 0), TimeSpan.Zero));
        Assert.HasCount(1, source.Reservations);
        Assert.IsTrue(source.TryTransferTo(destination, body, new(0, 0), TimeSpan.Zero));
        Assert.IsEmpty(source.Reservations);
        MapObject transferred = manager.ControlledObject!;
        Assert.AreNotSame(body, transferred);
        Assert.AreSame(body.EventReceiver, transferred.EventReceiver);
        Assert.AreEqual(body.Id, transferred.Id);
        Assert.IsFalse(body.IsActive);
        Assert.IsTrue(transferred.IsActive);
        manager.Deactivate();
        manager.Reactivate();
        Assert.IsFalse(body.IsActive);
        Assert.IsFalse(source.Positions.ContainsKey(Guid.Parse(body.Id)));
        Assert.AreEqual(new HexCoordinate(0, 0), destination.Positions[Guid.Parse(body.Id)]);
        Assert.IsGreaterThan(version, manager.ControlVersion);
        Assert.IsTrue(destination.TryTransferTo(source, transferred, new(1, 0), TimeSpan.Zero));
        Assert.AreSame(body, manager.ControlledObject);
        Assert.AreEqual(source.Layout.ToWorld(new(1, 0)), body.Position);
        manager.Clear();
        Assert.IsFalse(source.Positions.ContainsKey(Guid.Parse(body.Id)));
        TestContext.WriteLine("Board transfer retained the unit and receiver, invalidated control, and kept old map representations inactive.");
    }

    [TestMethod]
    public void GridTransferCannotBeBypassedAndNotificationsObserveCommittedState()
    {
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0)]);
        var first = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "first", "First", 1);
        var second = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "second", "Second", 1);
        var directory = new MapInstanceDirectory();
        directory.Register(first.Map);
        directory.Register(second.Map);
        MapObject body = CreateBody(new Context());
        UnitMapObjectManager manager = body.Manager!;
        Guid unitId = Guid.Parse(body.Id);
        Assert.IsTrue(first.TryPlace(body, new(0, 0)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            manager.SwitchParallelPlane(second.Map, second.Layout.ToWorld(new(1, 0)), TimeSpan.Zero));
        Assert.AreSame(body, manager.ControlledObject);
        Assert.AreEqual(new HexCoordinate(0, 0), first.Positions[unitId]);
        Assert.IsEmpty(second.Occupants);

        int notifications = 0;
        directory.VisibilityChanged += (_, _) =>
        {
            notifications++;
            Assert.AreSame(second.Map, manager.ControlledObject!.MapRuntime);
            Assert.IsFalse(first.Positions.ContainsKey(unitId));
            Assert.AreEqual(new HexCoordinate(1, 0), second.Positions[unitId]);
            Assert.IsFalse(body.IsActive);
            Assert.IsTrue(manager.ControlledObject.IsActive);
        };
        Assert.IsTrue(first.TryTransferTo(second, body, new(1, 0), TimeSpan.Zero));
        Assert.IsGreaterThanOrEqualTo(2, notifications);
        TestContext.WriteLine("Direct grid switching was rejected; both visibility notifications saw the committed occupancy and control.");
    }

    [TestMethod]
    public void TransferNotificationFailureDoesNotRollBackOnlyOccupancy()
    {
        var definition = new HexBoardDefinition([new(0, 0)]);
        var first = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "first", "First", 1);
        var second = new HexBoardMap(definition, 10, HexOrientation.PointyTop, "second", "Second", 1);
        var directory = new MapInstanceDirectory();
        directory.Register(first.Map);
        directory.Register(second.Map);
        MapObject body = CreateBody(new Context());
        Guid unitId = Guid.Parse(body.Id);
        Assert.IsTrue(first.TryPlace(body, new(0, 0)));
        directory.VisibilityChanged += (_, _) => throw new InvalidOperationException("Observer failed.");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            first.TryTransferTo(second, body, new(0, 0), TimeSpan.Zero));
        StringAssert.Contains(error.Message, "committed");
        Assert.AreSame(second.Map, body.Manager!.ControlledObject!.MapRuntime);
        Assert.IsFalse(body.IsActive);
        Assert.IsFalse(first.Positions.ContainsKey(unitId));
        Assert.AreEqual(new HexCoordinate(0, 0), second.Positions[unitId]);
        Assert.IsTrue(body.Manager.ControlledObject.IsActive);
        TestContext.WriteLine("A post-commit observer error was surfaced without splitting occupancy from control.");
    }

    private static MapObject CreateBody(Context context)
    {
        string id = Guid.NewGuid().ToString("N");
        var manager = new UnitMapObjectManager(id);
        var body = new MapObject(id, Vector2D.Zero, context.Factory.NewEventReceiver());
        manager.Initialize(body);
        return body;
    }

    private static HexBoardRuntime CreateBoard()
    {
        HexCoordinate[] cells =
        [
            new(-1, 0),
            new(0, -1),
            new(0, 0),
            new(0, 1),
            new(1, 0),
            new(1, -1),
        ];
        return new HexBoardRuntime(new HexBoardDefinition(cells));
    }
}
