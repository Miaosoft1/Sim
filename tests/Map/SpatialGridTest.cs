using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Spatial;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class SpatialGridTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RemovingAnEntryReleasesItsItemWithoutWaitingForCompaction()
    {
        var grid = new SpatialGrid<object>(MapSpace.Planar);
        var live = new object();
        grid.Add(live, Vector2D.Zero);
        WeakReference removed = AddAndRemoveItem(grid);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(removed.IsAlive);
        CollectionAssert.AreEqual(new[] { live }, grid.QueryBox(new(-1, -1), new(1, 1)).ToArray());
        GC.KeepAlive(grid);
        TestContext.WriteLine("A removed entry releases its object immediately while the remaining ordered query stays intact.");
    }

    [TestMethod]
    public void RemovingAValueTypeEntryReleasesItsReferencedObject()
    {
        var grid = new SpatialGrid<ReferenceKey>(MapSpace.Planar);
        WeakReference removed = AddAndRemoveValueTypeItem(grid);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(removed.IsAlive);
        Assert.IsEmpty(grid.QueryBox(new(-1, -1), new(1, 1)));
        GC.KeepAlive(grid);
        TestContext.WriteLine("An inactive value-type entry also releases the object referenced by its key.");
    }

    private readonly record struct ReferenceKey(object Value);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AddAndRemoveItem(SpatialGrid<object> grid)
    {
        var item = new object();
        var reference = new WeakReference(item);
        grid.Add(item, Vector2D.Zero);
        Assert.IsTrue(grid.Remove(item));
        return reference;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AddAndRemoveValueTypeItem(SpatialGrid<ReferenceKey> grid)
    {
        var item = new object();
        var reference = new WeakReference(item);
        var key = new ReferenceKey(item);
        grid.Add(key, Vector2D.Zero);
        Assert.IsTrue(grid.Remove(key));
        return reference;
    }

    [TestMethod]
    public void QueryCoversAllIntersectingCellsWithoutChangingExactGeometry()
    {
        var grid = new SpatialGrid<string>(MapSpace.Planar, 20);
        grid.Add("center", new(19, 19));
        grid.Add("near", new(22, 19));
        grid.Add("corner", new(24, 24));
        grid.Add("far", new(41, 41));
        for (int index = 0; index < 40; index++) grid.Add($"distant-{index}", new(1000 + index * 100, 1000));

        string[] candidates = grid.QueryBox(new(14, 14), new(24, 24), out var statistics).ToArray();
        CollectionAssert.AreEqual(new[] { "center", "near", "corner" }, candidates);
        Assert.AreEqual(4, statistics.CellsVisited);
        Assert.AreEqual(3, statistics.EntriesExamined);
        Assert.AreEqual(3, statistics.Candidates);
        var positions = new Dictionary<string, Vector2D>
        {
            ["center"] = new(19, 19), ["near"] = new(22, 19), ["corner"] = new(24, 24)
        };
        CollectionAssert.AreEqual(new[] { "center", "near" },
            candidates.Where(item => positions[item].DistanceTo(new(19, 19)) <= 5).ToArray());
        TestContext.WriteLine("Four-cell candidates include the neighboring unit without accepting the distant corner.");
    }

    [TestMethod]
    public void WrappedQueriesHandleSeamsAndNonDivisibleMapSizes()
    {
        var grid = new SpatialGrid<string>(MapSpace.Toroidal(103, 97), 20);
        grid.Add("right", new(101, 50));
        grid.Add("left", new(1, 50));
        grid.Add("edge", new(103, 50));
        grid.Add("middle", new(50, 50));

        CollectionAssert.AreEqual(new[] { "right", "left", "edge" },
            grid.QueryBox(new(96, 45), new(106, 55)).ToArray());
        CollectionAssert.AreEqual(new[] { "right", "left", "edge", "middle" },
            grid.QueryBox(new(-100, 0), new(200, 97)).ToArray());
        TestContext.WriteLine("Wrapped candidates cross the true 103-unit seam and are deduplicated.");
    }

    [TestMethod]
    public void MapUpdatesItsGridBeforePublishingChanges()
    {
        var context = new Context();
        var map = new MapRuntime(200, 200);
        var body = new MapObject("body", new(19, 19), context.Factory.NewEventReceiver());
        map.AddObject(body);
        bool observedNewPosition = false;
        map.VisibilityChanged += (changed, _) =>
        {
            if (ReferenceEquals(changed, body) && body.Position == new Vector2D(81, 19))
                observedNewPosition = map.QuerySpatialCandidates(new(80, 0), new(82, 20)).Contains(body);
        };

        body.Position = new(81, 19);
        Assert.IsTrue(observedNewPosition);
        Assert.IsFalse(map.QuerySpatialCandidates(new(0, 0), new(20, 20)).Contains(body));
        Assert.IsTrue(map.RemoveObject(body));
        Assert.IsFalse(map.QuerySpatialCandidates(new(80, 0), new(82, 20)).Contains(body));
        TestContext.WriteLine("Movement and removal are reflected in the map index before visibility notifications.");
    }

    [TestMethod]
    public void RegistrationAndRemovalNotificationsSeeCompleteState()
    {
        var context = new Context();
        var map = new MapRuntime(200, 200);
        var manager = new UnitMapObjectManager("unit");
        var body = new MapObject("unit", new(19, 19), context.Factory.NewEventReceiver());
        manager.Initialize(body);
        int additions = 0, removals = 0;
        void Check()
        {
            bool indexed = map.QuerySpatialCandidates(new(0, 0), new(30, 30)).Contains(body);
            if (body.MapRuntime == map)
            {
                Assert.IsTrue(map.Objects.Contains(body));
                Assert.AreSame(body, manager.Find(map));
                Assert.IsTrue(indexed);
                additions++;
            }
            else
            {
                Assert.IsFalse(map.Objects.Contains(body));
                Assert.IsNull(manager.Find(map));
                Assert.IsNull(manager.ControlledObject);
                Assert.IsFalse(indexed);
                removals++;
            }
        }
        manager.SpatialChanged += _ => Check();
        map.VisibilityChanged += (_, _) => Check();

        map.AddObject(body);
        Assert.IsTrue(map.RemoveObject(body));
        Assert.AreEqual(2, additions);
        Assert.AreEqual(2, removals);
        TestContext.WriteLine("Both spatial and visibility callbacks observe complete registration and removal.");
    }

    [TestMethod]
    public void RandomQueriesAndNearestMatchesBruteForce()
    {
        foreach (MapSpace space in new[] { MapSpace.Planar, MapSpace.Toroidal(103, 97) })
        {
            var grid = new SpatialGrid<int>(space, 20);
            var positions = new Dictionary<int, Vector2D>();
            var random = new Random(4837);
            for (int id = 0; id < 150; id++)
            {
                var position = new Vector2D(random.NextDouble() * 103, random.NextDouble() * 97);
                positions.Add(id, position);
                grid.Add(id, position);
            }
            positions.Add(150, new(103, 97));
            grid.Add(150, positions[150]);

            for (int query = 0; query < 120; query++)
            {
                int moved = random.Next(positions.Count);
                positions[moved] = new(random.NextDouble() * 103, random.NextDouble() * 97);
                grid.Update(moved, positions[moved]);
                var center = new Vector2D(random.NextDouble() * 103, random.NextDouble() * 97);
                double radiusX = random.NextDouble() * 90, radiusY = random.NextDouble() * 90;
                bool InBox(Vector2D point)
                {
                    Vector2D delta = space.Delta(center, point);
                    return Math.Abs(delta.X) <= radiusX && Math.Abs(delta.Y) <= radiusY;
                }
                int[] expected = positions.Where(pair => InBox(pair.Value)).Select(pair => pair.Key).ToArray();
                int[] actual = grid.QueryBox(new(center.X - radiusX, center.Y - radiusY),
                        new(center.X + radiusX, center.Y + radiusY))
                    .Where(id => InBox(positions[id])).ToArray();
                CollectionAssert.AreEqual(expected, actual, $"wrapped={space.IsWrapped}, query={query}");

                int nearest = positions.Where(pair => pair.Key % 3 == 0)
                    .OrderBy(pair => space.Distance(center, pair.Value)).ThenBy(pair => pair.Key).First().Key;
                Assert.IsTrue(grid.TryFindNearest(center, id => id % 3 == 0, (a, b) => a.CompareTo(b), out int found));
                Assert.AreEqual(nearest, found, $"wrapped={space.IsWrapped}, query={query}");
            }
        }
        TestContext.WriteLine("240 fixed-seed box and nearest-neighbor queries matched brute force.");
    }

    [TestMethod]
    public void ExtremeFinitePositionsRemainConservative()
    {
        var grid = new SpatialGrid<string>(MapSpace.Planar, 20);
        grid.Add("positive", new(double.MaxValue, 0));
        grid.Add("negative", new(-double.MaxValue, 0));
        Assert.IsTrue(grid.QueryBox(new(double.MaxValue, 0), new(double.MaxValue, 0)).Contains("positive"));
        Assert.IsTrue(grid.QueryBox(new(-double.MaxValue, 0), new(-double.MaxValue, 0)).Contains("negative"));
        TestContext.WriteLine("Coordinates beyond integer cell keys are retained as conservative candidates.");
    }

    [TestMethod]
    public void NegativeSubnormalCoordinatesShareTheirMathematicalCell()
    {
        var grid = new SpatialGrid<int>(MapSpace.Planar);
        grid.Add(0, new(-double.Epsilon, 0));
        grid.Add(1, new(-64 * double.Epsilon, 0));
        for (int id = 2; id < 82; id++) grid.Add(id, new(id * 100, 100));

        var bound = new Vector2D(-64 * double.Epsilon, 0);
        IReadOnlyList<int> candidates = grid.QueryBox(bound, bound, out var statistics);
        Assert.Contains(0, candidates);
        Assert.Contains(1, candidates);
        Assert.IsGreaterThan(0, statistics.CellsVisited);
        Assert.IsTrue(grid.AnyInBox(bound, bound, id => id == 0));
        grid.Update(0, new(double.Epsilon, 0));
        Assert.DoesNotContain(0, grid.QueryBox(bound, bound));
        Assert.IsFalse(grid.AnyInBox(bound, bound, id => id == 0));
        TestContext.WriteLine("Negative subnormal values use the same floor-based cell even when division underflows.");
    }

    [TestMethod]
    public void ExactBoundariesAndNearestTiesAreConservative()
    {
        var grid = new SpatialGrid<string>(MapSpace.Toroidal(100, 100), 20);
        grid.Add("at-zero", new(0, 0));
        grid.Add("at-width", new(100, 100));
        grid.Add("at-radius", new(3, 0));
        grid.Add("outside-radius", new(Math.BitIncrement(3d), 0));
        grid.Add("left", new(98, 0));
        grid.Add("right", new(2, 0));
        var candidates = grid.QueryBox(new(0, 0), new(3, 0));
        foreach (string expected in new[] { "at-zero", "at-width", "at-radius", "outside-radius", "right" })
            Assert.IsTrue(candidates.Contains(expected), expected);
        Assert.IsTrue(grid.QueryBox(new(-2, 0), new(3, 0)).Contains("left"));
        Assert.IsTrue(grid.AnyInBox(new(0, 0), new(3, 0), item => item == "at-width"));
        Assert.IsTrue(grid.TryFindNearest(new(0, 0), item => item is "left" or "right",
            string.CompareOrdinal, out string nearest));
        Assert.AreEqual("left", nearest);
        Assert.IsFalse(grid.TryFindNearest(new(0, 0), _ => false, string.CompareOrdinal, out _));
        TestContext.WriteLine("Inclusive seams, adjacent floating-point values, nearest ties and empty matches were checked.");
    }

    [TestMethod]
    public void NearestLowerBoundsPreserveBoundaryAndCornerTies()
    {
        int comparisons = 0;
        foreach (MapSpace space in new[] { MapSpace.Planar, MapSpace.Toroidal(103, 97) })
        {
            var grid = new SpatialGrid<int>(space, 20);
            double[] xs = [0, Math.BitDecrement(20), 20, Math.BitIncrement(20),
                Math.BitDecrement(103), 103, Math.BitIncrement(103), -1];
            double[] ys = [0, Math.BitDecrement(20), 20, Math.BitIncrement(20),
                Math.BitDecrement(97), 97, Math.BitIncrement(97), -1];
            var positions = new List<Vector2D>();
            foreach (double x in xs)
            foreach (double y in ys)
            {
                positions.Add(new(x, y));
                grid.Add(positions.Count - 1, positions[^1]);
            }
            var random = new Random(28112);
            for (int index = 0; index < 300; index++)
            {
                Vector2D origin = index < positions.Count ? positions[index] :
                    new(random.NextDouble() * 105 - 1, random.NextDouble() * 99 - 1);
                bool Eligible(int id) => id % 4 == index % 4;
                int expected = Enumerable.Range(0, positions.Count).Where(Eligible)
                    .OrderBy(id => space.Distance(origin, positions[id])).ThenBy(id => id).First();
                Assert.IsTrue(grid.TryFindNearest(origin, Eligible, (left, right) => left.CompareTo(right), out int actual));
                Assert.AreEqual(expected, actual, $"wrapped={space.IsWrapped}, origin={origin}");
                comparisons++;
            }
        }
        TestContext.WriteLine($"{comparisons} nearest searches matched exact order around grid lines, seams and corners.");
    }

    [TestMethod]
    public void RepeatedRemovalAndRegistrationKeepOrderedScanStable()
    {
        var grid = new SpatialGrid<int>(MapSpace.Planar, 20);
        for (int id = 0; id < 200; id++) grid.Add(id, new(id, 0));
        for (int id = 0; id < 150; id++) Assert.IsTrue(grid.Remove(id));
        for (int id = 0; id < 20; id++) grid.Add(id, new(id, 0));
        int[] expected = Enumerable.Range(150, 50).Concat(Enumerable.Range(0, 20)).ToArray();
        CollectionAssert.AreEqual(expected,
            grid.QueryBox(new(-1000, -1000), new(1000, 1000), out var statistics).ToArray());
        Assert.AreEqual(0, statistics.CellsVisited);
        Assert.AreEqual(expected.Length, statistics.Candidates);
        TestContext.WriteLine("Repeated removal and re-registration retained insertion order after compaction.");
    }

    [TestMethod]
    public void NearestFrontierFallsBackWithoutDuplicatePredicateCalls()
    {
        foreach (MapSpace space in new[] { MapSpace.Planar, MapSpace.Toroidal(1033, 977) })
        {
            var grid = new SpatialGrid<int>(space, 20);
            var random = new Random(71012);
            Vector2D[] positions = Enumerable.Range(0, 400)
                .Select(_ => new Vector2D(random.NextDouble() * 1033, random.NextDouble() * 977)).ToArray();
            for (int id = 0; id < positions.Length; id++) grid.Add(id, positions[id]);
            for (int query = 0; query < 200; query++)
            {
                var origin = query % 10 == 0 ? new Vector2D(1e12, -1e12) :
                    new(random.NextDouble() * 1033, random.NextDouble() * 977);
                var checkedIds = new HashSet<int>();
                bool Eligible(int id)
                {
                    Assert.IsTrue(checkedIds.Add(id), $"Duplicate predicate call for {id}.");
                    return query % 7 != 0 && id % 29 == query % 29;
                }
                bool found = grid.TryFindNearest(origin, Eligible, (left, right) => left.CompareTo(right), out int actual);
                int[] expected = Enumerable.Range(0, positions.Length)
                    .Where(id => query % 7 != 0 && id % 29 == query % 29)
                    .OrderBy(id => space.Distance(origin, positions[id])).ThenBy(id => id).ToArray();
                Assert.AreEqual(expected.Length > 0, found);
                if (found) Assert.AreEqual(expected[0], actual, $"wrapped={space.IsWrapped}, query={query}");
                else Assert.HasCount(positions.Length, checkedIds);
            }
        }
        TestContext.WriteLine("400 sparse and empty nearest searches matched direct results without repeated predicates.");
    }
}
