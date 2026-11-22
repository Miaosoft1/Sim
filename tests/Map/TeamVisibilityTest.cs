using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Map.EventData;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class TeamVisibilityTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void FixedAreaPreservesTheOriginalProjectionAndRejectsInvalidGeometry()
    {
        var area = MapVisibilityArea.Default;
        Assert.AreEqual(34 * (1280d / 720), area.Width, 1e-12);
        Assert.AreEqual(34 * Math.Sqrt(1 + 2 * Math.Pow(17d / 23, 2)), area.Depth, 1e-12);
        var origin = new Vector2D(100, 100);
        var map = new MapRuntime(500, 500);
        foreach (int x in new[] { -1, 1 })
        foreach (int y in new[] { -1, 1 })
        {
            Vector2D Point(double right, double depth) => new(
                origin.X + right * area.Direction.Y + depth * area.Direction.X,
                origin.Y - right * area.Direction.X + depth * area.Direction.Y);
            Assert.IsTrue(area.Contains(map, origin, Point(x * (area.Width / 2 - 1e-8),
                y * (area.Depth / 2 - 1e-8))));
            Assert.IsFalse(area.Contains(map, origin, Point(x * (area.Width / 2 + 1e-8), 0)));
            Assert.IsFalse(area.Contains(map, origin, Point(0, y * (area.Depth / 2 + 1e-8))));
        }
        foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MapVisibilityArea(invalid, 1, new(1, 1)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MapVisibilityArea(1, invalid, new(1, 1)));
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MapVisibilityArea(1, 1, new(0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MapVisibilityArea(1, 1, new(double.NaN, 1)));
        TestContext.WriteLine($"Fixed area: {area.Width:R} x {area.Depth:R}, direction={area.Direction}.");
    }

    [TestMethod]
    public void FixedAreaAndStealthApplyAtExactBoundaries()
    {
        var area = MapVisibilityArea.Default;
        Assert.AreEqual(60.444444, area.Width, 0.000001);
        Assert.AreEqual(49.184118, area.Depth, 0.000001);
        Assert.AreEqual(38.963471, area.DefaultVisionRange, 0.000001);
        Assert.AreEqual(1 / Math.Sqrt(2), area.Direction.X);
        Assert.AreEqual(area.Direction.X, area.Direction.Y);

        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(500, 500);
        var observer = new MapObject("observer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(150, 100), receiver);
        map.AddObject(observer);
        map.AddObject(target);
        var manager = new MapVisibilityManager(area);
        MapVisibilityObserver[] observers = [new("blue", observer, 60)];
        Assert.IsTrue(manager.IsVisible("blue", target, 10, observers));
        Assert.IsFalse(manager.IsVisible("blue", target, 10.001, observers));
        target.Position = new Vector2D(110, 100);
        Assert.IsTrue(manager.IsVisible("blue", target, 1000, observers));
        Assert.IsFalse(manager.IsVisible("red", target, 0, observers));
    }

    [TestMethod]
    public void TeamSharesVisionAcrossInstancesButCannotSeeAcrossInstancesDirectly()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var firstMap = new MapRuntime(500, 500);
        var secondMap = new MapRuntime(500, 500);
        var first = new MapObject("first", new Vector2D(100, 100), receiver);
        var scout = new MapObject("scout", new Vector2D(100, 100), receiver);
        var target = new MapObject("enemy", new Vector2D(110, 100), receiver);
        firstMap.AddObject(first);
        secondMap.AddObject(scout);
        secondMap.AddObject(target);
        var manager = new MapVisibilityManager(MapVisibilityArea.Default);
        MapVisibilityObserver[] observers = [new("blue", first, 40), new("blue", scout, 40)];
        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers));
        Assert.IsFalse(manager.IsVisible("blue", target, 0, [observers[0]]));
        scout.IsActive = false;
        Assert.IsFalse(manager.IsVisible("blue", target, 0, observers));
    }

    [TestMethod]
    public void NpcBodiesAndSoulsContributeIndependentlyUntilTheyLeaveTheirMap()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var root = new MapRuntime(500, 500);
        var body = new MapObject("scout", new Vector2D(100, 100), receiver, kind: MapObjectKind.Summon);
        var representations = new UnitMapObjectManager(body.Id);
        representations.Initialize(body);
        root.AddObject(body);
        var remote = root.CreateTemporaryMap(new Vector2D(200, 200), isMainPlane: false);
        var soul = representations.Enter(remote, new Vector2D(200, 200), TimeSpan.Zero,
            keepSourceVisible: true, appearanceOverlay: true);
        var enemy = new MapObject("enemy", new Vector2D(205, 200), receiver);
        remote.AddObject(enemy);
        MapVisibilityObserver[] observers = [new("blue", body, 10), new("blue", soul, 10)];
        var manager = new MapVisibilityManager(MapVisibilityArea.Default);

        Assert.IsTrue(body.IsVisible);
        Assert.IsTrue(soul.HasAppearanceOverlay);
        Assert.IsTrue(manager.IsVisible("blue", enemy, 0, observers));
        Assert.IsFalse(manager.IsVisible("blue", enemy, 0, [observers[0]]));
        representations.Return(TimeSpan.Zero);
        Assert.IsFalse(manager.IsVisible("blue", enemy, 0, observers));
        TestContext.WriteLine("A living NPC soul revealed its map independently of its physical body.");
    }

    [TestMethod]
    public void CircularVisionBoundaryIncludesExactPointsButNotNearbyTangents()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(500, 500);
        var observer = new MapObject("observer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(103, 104), receiver);
        map.AddObject(observer);
        map.AddObject(target);
        var manager = new MapVisibilityManager(new MapVisibilityArea(1, Math.Sqrt(2), new(0, 1)));
        MapVisibilityObserver[] observers = [new("blue", observer, 5)];

        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers));
        target.Position = new Vector2D(Math.BitIncrement(100), 105);
        Assert.IsFalse(manager.IsVisible("blue", target, 0, observers));
        target.Position = new Vector2D(100, 105);
        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers));
        TestContext.WriteLine("Exact circular boundaries remain inclusive without rounding a nearby tangent inward.");
    }

    [TestMethod]
    public void VisibilityOverridesRespectTheAreaAndOtherTeamObservers()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(500, 500);
        var first = new MapObject("first", new Vector2D(100, 100), receiver);
        var second = new MapObject("second", new Vector2D(104, 100), receiver);
        var target = new MapObject("target", new Vector2D(104, 100), receiver);
        map.AddObject(first);
        map.AddObject(second);
        map.AddObject(target);
        var manager = new MapVisibilityManager(new MapVisibilityArea(1, Math.Sqrt(2), new(0, 1)));
        MapVisibilityObserver[] observers = [new("blue", first, 5), new("blue", second, 5)];
        IMapVisibilityRule[] rules = [new DenyVisibilityRule()];

        Assert.IsFalse(manager.IsVisible("blue", target, 0, [observers[0]], rules));
        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers, rules));
        var intervals = manager.TraceVisibility("blue", target, 0, [observers[0]], target,
            new Vector2D(96, 100), new Vector2D(104, 100), rules);
        Assert.IsFalse(intervals.Any(interval => interval.Start <= 0.1 && interval.End >= 0.1));
        Assert.IsTrue(intervals.Any(interval => interval.Start <= 0.5 && interval.End >= 0.5));
        TestContext.WriteLine("A deny override suppresses distance vision but not the screen guarantee or another observer.");
    }

    [TestMethod]
    public void ObserverRulesFollowItsRepresentationsWithoutRevealingToOtherTeams()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var initial = new MapRuntime(500, 500);
        var remote = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(170, 100), receiver);
        var opponent = new MapObject("opponent", new Vector2D(100, 100), receiver);
        var representations = new UnitMapObjectManager(viewer.Id);
        representations.Initialize(viewer);
        initial.AddObject(viewer);
        remote.AddObject(target);
        remote.AddObject(opponent);
        var rule = new RevealPairRule(viewer.Id, target.Id);
        using IDisposable first = representations.RegisterVisibilityRule(rule);
        using IDisposable second = representations.RegisterVisibilityRule(rule);
        var manager = new MapVisibilityManager(new MapVisibilityArea(1, Math.Sqrt(2), new(0, 1)));
        MapVisibilityObserver[] observers = [new("blue", viewer, 5), new("red", opponent, 5)];
        Assert.IsFalse(manager.IsVisible("blue", target, 0, observers));

        MapObject remoteViewer = representations.Enter(remote, new Vector2D(100, 100), TimeSpan.Zero,
            keepSourceVisible: false);
        observers = [new("blue", remoteViewer, 5), new("red", opponent, 5)];
        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers));
        Assert.IsFalse(manager.IsVisible("red", target, 0, observers));
        var intervals = manager.TraceVisibility("blue", target, 0, observers, target,
            new Vector2D(160, 100), new Vector2D(180, 100));
        Assert.AreEqual(new MapVisibilityInterval(0, 1), intervals.Single());

        first.Dispose();
        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers));
        second.Dispose();
        Assert.IsFalse(manager.IsVisible("blue", target, 0, observers));
        TestContext.WriteLine("Observer-owned reveal rules follow active representations and remain team-scoped.");
    }

    [TestMethod]
    [DataRow(24d, 7d, 25d)]
    [DataRow(20d, 21d, 29d)]
    [DataRow(84d, 13d, 85d)]
    public void CircularVisionIncludesPythagoreanBoundary(double x, double y, double radius)
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(500, 500);
        var observer = new MapObject("observer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(100 + x, 100 + y), receiver);
        map.AddObject(observer);
        map.AddObject(target);
        var manager = new MapVisibilityManager(new MapVisibilityArea(1, Math.Sqrt(2), new(0, 1)));
        MapVisibilityObserver[] observers = [new("blue", observer, radius)];

        Assert.IsTrue(manager.IsVisible("blue", target, 0, observers));
        target.Position = new Vector2D(Math.BitIncrement(100 + x), 100 + y);
        Assert.IsFalse(manager.IsVisible("blue", target, 0, observers));
        TestContext.WriteLine($"Verified exact radius {radius} and the next representable point outside it.");
    }

    [TestMethod]
    public void TorusAreaUsesWrappedDistance()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var definition = new MapDefinition("world", "world", 500, 500, "Overworld", "Torus");
        var map = new MapRuntime(definition);
        var viewer = new MapObject("viewer", new Vector2D(1, 200), receiver);
        var target = new MapObject("target", new Vector2D(499, 200), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var manager = new MapVisibilityManager(MapVisibilityArea.Default);
        Assert.IsTrue(manager.IsVisible("blue", target, 1000, [new("blue", viewer, 0)]));
    }

    [TestMethod]
    public void AreaCoversEachEdgeAndCorner()
    {
        var area = new MapVisibilityArea(20, 20 * Math.Sqrt(2), new(0, 1));
        var map = new MapRuntime(500, 500);
        var origin = new Vector2D(100, 100);
        double halfWidth = area.Width / 2;
        double halfDepth = area.Depth / 2;

        Assert.IsTrue(area.Contains(map, origin, new Vector2D(100 + halfWidth, 100)));
        Assert.IsTrue(area.Contains(map, origin, new Vector2D(100 - halfWidth, 100)));
        Assert.IsTrue(area.Contains(map, origin, new Vector2D(100, 100 + halfDepth - 1e-8)));
        Assert.IsTrue(area.Contains(map, origin, new Vector2D(100, 100 - halfDepth + 1e-8)));
        foreach (int x in new[] { -1, 1 })
        foreach (int y in new[] { -1, 1 })
        {
            Assert.IsTrue(area.Contains(map, origin,
                new Vector2D(100 + x * (halfWidth - 1e-8), 100 + y * (halfDepth - 1e-8))));
            Assert.IsFalse(area.Contains(map, origin,
                new Vector2D(100 + x * (halfWidth + 1e-8), 100 + y * halfDepth)));
            Assert.IsFalse(area.Contains(map, origin,
                new Vector2D(100 + x * halfWidth, 100 + y * (halfDepth + 1e-8))));
        }

        TestContext.WriteLine("All edges and corners obey the authoritative world-space rectangle.");
    }

    [TestMethod]
    public void OnlyLiveActiveVisibleObserversInTheSameMapContribute()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(105, 100), receiver);
        var viewerManager = new UnitMapObjectManager("viewer");
        var targetManager = new UnitMapObjectManager("target");
        viewerManager.Initialize(viewer);
        targetManager.Initialize(target);
        map.AddObject(viewer);
        map.AddObject(target);
        var manager = new MapVisibilityManager(MapVisibilityArea.Default);

        Assert.IsTrue(manager.IsVisible("blue", target, 0, [new("blue", viewer, 0)]));
        Assert.IsFalse(manager.IsVisible("blue", target, 0, [new("blue", viewer, 0, IsAlive: false)]));
        viewer.IsActive = false;
        Assert.IsFalse(manager.IsVisible("blue", target, 0, [new("blue", viewer, 0)]));
        viewer.IsActive = true;
        viewerManager.SetState(map, isVisible: false);
        Assert.IsFalse(manager.IsVisible("blue", target, 0, [new("blue", viewer, 0)]));
        viewerManager.SetState(map, isVisible: true);
        targetManager.SetState(map, isVisible: false);
        Assert.IsFalse(manager.IsVisible("blue", target, 0, [new("blue", viewer, 0)]));
        targetManager.SetState(map, isVisible: true);
        map.RemoveObject(viewer);
        map.RemoveObject(target);
        Assert.IsFalse(manager.IsVisible("blue", target, 0, [new("blue", viewer, 100)]));
        TestContext.WriteLine("Dead, inactive, invisible, and mapless observers cannot reveal a target.");
    }

    [TestMethod]
    public void SweptTargetAndObserverCrossTheEntireVisionBetweenUpdates()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(120, 100), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var manager = new MapVisibilityManager(new MapVisibilityArea(2, 2 * Math.Sqrt(2), new(0, 1)));
        MapVisibilityObserver[] observers = [new("blue", viewer, 5)];

        var crossing = manager.TraceVisibility("blue", target, 0, observers, target,
            new Vector2D(80, 100), target.Position).Single();
        Assert.AreEqual(0.375, crossing.Start, 1e-10);
        Assert.AreEqual(0.625, crossing.End, 1e-10);

        viewer.Position = new Vector2D(120, 100);
        target.Position = new Vector2D(100, 100);
        var movingObserver = manager.TraceVisibility("blue", target, 0, observers, viewer,
            new Vector2D(80, 100), viewer.Position).Single();
        Assert.AreEqual(crossing.Start, movingObserver.Start, 1e-10);
        Assert.AreEqual(crossing.End, movingObserver.End, 1e-10);

        var screenOnly = manager.TraceVisibility("blue", target, 1000, observers, viewer,
            new Vector2D(80, 100), viewer.Position).Single();
        Assert.AreEqual(0.475, screenOnly.Start, 1e-10);
        Assert.AreEqual(0.525, screenOnly.End, 1e-10);
        TestContext.WriteLine("Full-segment visibility detects both moving targets and moving team observers.");
    }

    [TestMethod]
    public void SweptVisibilityUnionsObserversAndHandlesTorusSeams()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(new MapDefinition("world", "world", 100, 100, "Overworld", "Torus"));
        var first = new MapObject("first", new Vector2D(0, 50), receiver);
        var second = new MapObject("second", new Vector2D(7, 50), receiver);
        var target = new MapObject("target", new Vector2D(10, 50), receiver);
        map.AddObject(first);
        map.AddObject(second);
        map.AddObject(target);
        var manager = new MapVisibilityManager(new MapVisibilityArea(2, 2 * Math.Sqrt(2), new(0, 1)));
        MapVisibilityObserver[] observers = [new("blue", first, 3), new("blue", second, 3)];
        var intervals = manager.TraceVisibility("blue", target, 0, observers, target,
            new Vector2D(90, 50), target.Position);

        Assert.HasCount(2, intervals);
        Assert.AreEqual(0.35, intervals[0].Start, 1e-10);
        Assert.AreEqual(0.65, intervals[0].End, 1e-10);
        Assert.AreEqual(0.7, intervals[1].Start, 1e-10);
        Assert.AreEqual(1, intervals[1].End, 1e-10);
        TestContext.WriteLine("Wrapped paths preserve separate visibility intervals from the team observer union.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void MovementStepsStopAtVisibilityBoundaries(bool movingObserver, bool instant)
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var map = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new Vector2D(movingObserver ? 80 : 100, 100), receiver);
        var target = new MapObject("target", new Vector2D(movingObserver ? 100 : 80, 100), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var area = new MapVisibilityArea(2, 2 * Math.Sqrt(2), new(0, 1));
        MapVisibilityObserver[] observers = [new("blue", viewer, 5)];
        var scene = new MapVisibilityScene(["blue"], observers, [new(target, 0)]);
        using var lease = map.RegisterMovementStepLimiter(new MapVisibilityMovementLimiter(_ => new(area, scene)));
        var manager = new MapVisibilityManager(area);
        var updates = new List<(double X, bool Visible, TimeSpan Time)>();
        context.EventProcessed += ev =>
        {
            if (ev is MapMovementUpdatedEvent update)
                updates.Add((update.Position.X, manager.IsVisible("blue", target, 0, observers), update.At));
        };
        MapObject moving = movingObserver ? viewer : target;
        var move = MapMovementEvent.Schedule(context, receiver, moving, new Vector2D(120, 100),
            instant ? 0 : 1000, TimeSpan.FromSeconds(1),
            mode: instant ? MovementMode.Instant : MovementMode.Linear);
        context.Dispatch(move).ToList();
        ContextRunResult result = context.RunFor(1000);

        Assert.AreEqual(StopReason.QueueEmpty, result.Reason, "The boundary limiter must terminate.");
        Assert.IsGreaterThanOrEqualTo(3, updates.Count);
        Assert.IsTrue(updates[0].Visible);
        Assert.AreEqual(95, updates[0].X, 1e-8);
        Assert.IsFalse(updates[1].Visible);
        Assert.AreEqual(105, updates[1].X, 1e-8);
        Assert.AreEqual(120, updates[^1].X, 1e-8);
        if (!instant)
        {
            Assert.AreEqual(0.015, updates[0].Time.TotalSeconds, 1e-6);
            Assert.AreEqual(0.025, updates[1].Time.TotalSeconds, 1e-6);
            Assert.AreEqual(0.040, updates[^1].Time.TotalSeconds, 1e-6);
        }
        else Assert.IsTrue(updates.All(update => update.Time == TimeSpan.Zero));
        TestContext.WriteLine($"moving-observer={movingObserver}, instant={instant}, " +
            $"updates={string.Join(", ", updates.Select(update => $"{update.X:F6}:{update.Visible}@{update.Time}"))}");
    }

    [TestMethod]
    public void SweptIntervalsMatchPointVisibilityForRotatedAndWrappedMaps()
    {
        var random = new Random(79231);
        int samples = 0;
        foreach (bool torus in new[] { false, true })
        foreach (bool movingObserver in new[] { false, true })
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            var map = torus
                ? new MapRuntime(new MapDefinition("world", "world", 100, 100, "Overworld", "Torus"))
                : new MapRuntime(100, 100);
            var first = new MapObject("first", Vector2D.Zero, receiver);
            var second = new MapObject("second", Vector2D.Zero, receiver);
            var target = new MapObject("target", Vector2D.Zero, receiver);
            map.AddObject(first);
            map.AddObject(second);
            map.AddObject(target);
            var manager = new MapVisibilityManager(new MapVisibilityArea(12, 8 * Math.Sqrt(2), new(3, 4)));
            for (int scenario = 0; scenario < 80; scenario++)
            {
                Vector2D Point() => new(random.NextDouble() * 100, random.NextDouble() * 100);
                first.Position = Point();
                second.Position = Point();
                target.Position = Point();
                MapObject moving = movingObserver ? first : target;
                Vector2D from = moving.Position;
                Vector2D to = Point();
                moving.Position = to;
                double stealth = random.NextDouble() * 12;
                MapVisibilityObserver[] observers =
                [
                    new("blue", first, 5 + random.NextDouble() * 25),
                    new("blue", second, 5 + random.NextDouble() * 25)
                ];
                var intervals = manager.TraceVisibility("blue", target, stealth, observers, moving, from, to);
                double distance = map.Movement.Distance(from, to);
                for (int index = 0; index <= 100; index++)
                {
                    double fraction = index / 100d;
                    moving.Position = map.Movement.MoveTowards(from, to, distance * fraction);
                    bool expected = manager.IsVisible("blue", target, stealth, observers);
                    bool traced = intervals.Any(interval => interval.Start <= fraction && fraction <= interval.End);
                    Assert.AreEqual(expected, traced,
                        $"torus={torus}, observer={movingObserver}, scenario={scenario}, fraction={fraction}");
                    samples++;
                }
            }
        }
        TestContext.WriteLine($"Compared {samples} swept samples with direct visibility decisions.");
    }

    [TestMethod]
    public void WrappedMovementSchedulesEntryAndExitAcrossTheMapSeam()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var map = new MapRuntime(new MapDefinition("world", "world", 100, 100, "Overworld", "Torus"));
        var viewer = new MapObject("viewer", new Vector2D(0, 50), receiver);
        var target = new MapObject("target", new Vector2D(90, 50), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var area = new MapVisibilityArea(2, 2 * Math.Sqrt(2), new(0, 1));
        MapVisibilityObserver[] observers = [new("blue", viewer, 3)];
        var scene = new MapVisibilityScene(["blue"], observers, [new(target, 0)]);
        using var lease = map.RegisterMovementStepLimiter(new MapVisibilityMovementLimiter(_ => new(area, scene)));
        var manager = new MapVisibilityManager(area);
        var updates = new List<(double X, bool Visible, TimeSpan Time)>();
        context.EventProcessed += ev =>
        {
            if (ev is MapMovementUpdatedEvent update)
                updates.Add((update.Position.X, manager.IsVisible("blue", target, 0, observers), update.At));
        };
        context.Dispatch(MapMovementEvent.Schedule(context, receiver, target, new Vector2D(10, 50),
            1000, TimeSpan.FromSeconds(1))).ToList();
        var result = context.RunFor(1000);

        Assert.AreEqual(StopReason.QueueEmpty, result.Reason);
        Assert.IsGreaterThanOrEqualTo(3, updates.Count);
        Assert.AreEqual(97, updates[0].X, 1e-8);
        Assert.IsTrue(updates[0].Visible);
        Assert.AreEqual(3, updates[1].X, 1e-8);
        Assert.IsFalse(updates[1].Visible);
        Assert.AreEqual(10, updates[^1].X, 1e-8);
        Assert.AreEqual(0.007, updates[0].Time.TotalSeconds, 1e-6);
        Assert.AreEqual(0.013, updates[1].Time.TotalSeconds, 1e-6);
        TestContext.WriteLine($"Wrapped visibility updates: {string.Join(", ", updates)}.");
    }

    [TestMethod]
    public void InstantMovementDoesNotSkipARepresentableNarrowArea()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var map = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(99, 100), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var area = new MapVisibilityArea(1e-13, 1e-13 * Math.Sqrt(2), new(0, 1));
        MapVisibilityObserver[] observers = [new("blue", viewer, 0)];
        var scene = new MapVisibilityScene(["blue"], observers, [new(target, 0)]);
        using var lease = map.RegisterMovementStepLimiter(new MapVisibilityMovementLimiter(_ => new(area, scene)));
        var manager = new MapVisibilityManager(area);
        var updates = new List<(double X, bool Visible)>();
        context.EventProcessed += ev =>
        {
            if (ev is MapMovementUpdatedEvent update)
                updates.Add((update.Position.X, manager.IsVisible("blue", target, 0, observers)));
        };
        context.Dispatch(MapMovementEvent.Schedule(context, receiver, target, new Vector2D(101, 100),
            0, TimeSpan.FromSeconds(1), mode: MovementMode.Instant)).ToList();
        ContextRunResult result = context.RunFor(1000);

        TestContext.WriteLine($"Narrow-area updates: {string.Join(", ", updates.Select(update => $"{update.X:R}:{update.Visible}"))}.");
        Assert.AreEqual(StopReason.QueueEmpty, result.Reason);
        Assert.IsTrue(updates.Any(update => update.Visible), "A representable visible interval must not be skipped.");
        Assert.IsFalse(updates[^1].Visible);
        Assert.AreEqual(101, updates[^1].X);
    }

    [TestMethod]
    public void TangentMovementVisitsTheInclusiveVisionBoundary()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var map = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(90, 105), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var area = new MapVisibilityArea(2, 2 * Math.Sqrt(2), new(0, 1));
        MapVisibilityObserver[] observers = [new("blue", viewer, 5)];
        var scene = new MapVisibilityScene(["blue"], observers, [new(target, 0)]);
        using var lease = map.RegisterMovementStepLimiter(new MapVisibilityMovementLimiter(_ => new(area, scene)));
        var manager = new MapVisibilityManager(area);
        var updates = new List<(double X, bool Visible)>();
        context.EventProcessed += ev =>
        {
            if (ev is MapMovementUpdatedEvent update)
                updates.Add((update.Position.X, manager.IsVisible("blue", target, 0, observers)));
        };
        context.Dispatch(MapMovementEvent.Schedule(context, receiver, target, new Vector2D(110, 105),
            0, TimeSpan.FromSeconds(1), mode: MovementMode.Instant)).ToList();
        ContextRunResult result = context.RunFor(1000);

        TestContext.WriteLine($"Tangent updates ({updates.Count}): first {string.Join(", ", updates.Take(5).Select(update => $"{update.X:R}:{update.Visible}"))}; last {string.Join(", ", updates.TakeLast(5).Select(update => $"{update.X:R}:{update.Visible}"))}.");
        Assert.AreEqual(StopReason.QueueEmpty, result.Reason);
        Assert.IsTrue(updates.Any(update => update.Visible), "The vision boundary is inclusive.");
        Assert.IsFalse(updates[^1].Visible);
        Assert.AreEqual(110, updates[^1].X);
    }

    [TestMethod]
    [DataRow(4e-7, 1e-7)]
    [DataRow(4e-10, 1e-10)]
    public void ShortWrappedMovementDoesNotSkipTheVisibilityInterval(double offset, double areaSize)
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new MapMovementHandler());
        var map = new MapRuntime(new MapDefinition("world", "world", 500, 500, "Overworld", "Torus"));
        var viewer = new MapObject("viewer", new Vector2D(100, 100), receiver);
        var target = new MapObject("target", new Vector2D(100 - offset, 100), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var area = new MapVisibilityArea(areaSize, areaSize * Math.Sqrt(2), new(0, 1));
        MapVisibilityObserver[] observers = [new("blue", viewer, 0)];
        var scene = new MapVisibilityScene(["blue"], observers, [new(target, 0)]);
        using var lease = map.RegisterMovementStepLimiter(new MapVisibilityMovementLimiter(_ => new(area, scene)));
        var manager = new MapVisibilityManager(area);
        var updates = new List<(double X, bool Visible)>();
        context.EventProcessed += ev =>
        {
            if (ev is MapMovementUpdatedEvent update)
                updates.Add((update.Position.X, manager.IsVisible("blue", target, 0, observers)));
        };
        var destination = new Vector2D(100 + offset, 100);
        context.Dispatch(MapMovementEvent.Schedule(context, receiver, target, destination,
            0, TimeSpan.FromSeconds(1), mode: MovementMode.Instant)).ToList();
        ContextRunResult result = context.RunFor(1000);

        TestContext.WriteLine($"Short wrapped updates: {string.Join(", ", updates.Select(update => $"{update.X:R}:{update.Visible}"))}.");
        Assert.AreEqual(StopReason.QueueEmpty, result.Reason);
        Assert.IsTrue(updates.Any(update => update.Visible));
        Assert.IsFalse(updates[^1].Visible);
        Assert.AreEqual(destination.X, updates[^1].X);
    }

    private sealed class DenyVisibilityRule : IMapVisibilityRule
    {
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) => false;
    }

    private sealed class RevealPairRule(string viewerId, string targetId) : IMapVisibilityRule
    {
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) =>
            viewer.Id == viewerId && target.Id == targetId ? true : null;
    }
}
