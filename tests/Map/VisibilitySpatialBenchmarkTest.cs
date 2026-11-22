using System.Diagnostics;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Spatial;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VisibilitySpatialBenchmarkTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RepresentativeSnapshotWorkloads()
    {
        Measure("small", 6, 24, 200);
        Measure("sparse", 32, 160, 4000);
        Measure("dense", 32, 160, 120);
    }

    [TestMethod]
    public void RepresentativeGridQueriesIncludeIndexMaintenance()
    {
        MeasureGrid("small", 24, 200, 1, 50, wrapped: false);
        MeasureGrid("sparse-few-moves", 800, 4000, 1, 50, wrapped: false);
        MeasureGrid("sparse-many-moves", 800, 4000, 20, 50, wrapped: false);
        MeasureGrid("dense-many-moves", 800, 120, 20, 50, wrapped: false);
        MeasureGrid("long-range", 800, 4000, 20, 4000, wrapped: false);
        MeasureGrid("wrapped", 800, 103, 20, 20, wrapped: true);
    }

    [TestMethod]
    public void RepresentativeVisibilityPipelinesIncludeMapsRulesAndMovement()
    {
        MeasurePipeline("sparse-scoped-few", 4000, 40, 1, unboundedRule: false);
        MeasurePipeline("sparse-unbounded-many", 4000, 40, 16, unboundedRule: true);
        MeasurePipeline("dense-unbounded-many", 120, 40, 16, unboundedRule: true);
        MeasurePipeline("long-range-scoped-many", 4000, 4500, 16, unboundedRule: false);
    }

    private void Measure(string name, int observerCount, int targetCount, double spread)
    {
        const int warmups = 2, samples = 12, positionQueries = 64;
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(5000, 5000);
        var random = new Random(1729);
        Vector2D NextPosition() => new(100 + random.NextDouble() * spread, 100 + random.NextDouble() * spread);
        var observers = Enumerable.Range(0, observerCount).Select(index =>
        {
            var body = new MapObject($"observer-{index}", NextPosition(), receiver);
            map.AddObject(body);
            return new MapVisibilityObserver(index % 2 == 0 ? "blue" : "red", body, 50);
        }).ToArray();
        var targets = Enumerable.Range(0, targetCount).Select(index =>
        {
            var body = new MapObject($"target-{index}", NextPosition(), receiver);
            map.AddObject(body);
            return new MapVisibilityTarget(body, index % 3);
        }).ToArray();
        var scene = new MapVisibilityScene(["blue", "red"], observers, targets);
        var positions = Enumerable.Range(0, positionQueries).Select(_ => NextPosition()).ToArray();
        Vector2D originalPosition = targets[0].Body.Position;
        var builds = new List<double>(samples);
        var maintenance = new List<double>(samples);
        var updates = new List<double>(samples);
        var queries = new List<double>(samples);
        var buildBytes = new List<long>(samples);
        var maintenanceBytes = new List<long>(samples);
        var updateBytes = new List<long>(samples);
        var queryBytes = new List<long>(samples);
        int ruleEvaluations = 0, objectEvaluations = 0;

        for (int iteration = -warmups; iteration < samples; iteration++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
            double buildMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long builtBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            ruleEvaluations = snapshot.RuleEvaluations;
            objectEvaluations = snapshot.ObjectEvaluations;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            targets[0].Body.Position = new(4900, 4900);
            double maintenanceMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long maintainedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            MapVisibilitySnapshot updated = snapshot.Update(scene);
            double updateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long changedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            foreach (Vector2D position in positions)
                _ = updated.IsPositionVisible("blue", map, position);
            double queryMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long queriedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

            var rebuilt = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
            foreach (string team in scene.Teams)
            foreach (MapVisibilityTarget target in targets)
                Assert.AreEqual(rebuilt.IsVisible(team, target.Body), updated.IsVisible(team, target.Body));
            targets[0].Body.Position = originalPosition;

            if (iteration < 0) continue;
            builds.Add(buildMilliseconds);
            maintenance.Add(maintenanceMilliseconds);
            updates.Add(updateMilliseconds);
            queries.Add(queryMilliseconds);
            buildBytes.Add(builtBytes);
            maintenanceBytes.Add(maintainedBytes);
            updateBytes.Add(changedBytes);
            queryBytes.Add(queriedBytes);
        }

        TestContext.WriteLine(
            $"{name}: observers={observerCount}, targets={targetCount}, samples={samples}, " +
            $"full p50/p95={Percentile(builds, .50):F3}/{Percentile(builds, .95):F3}ms, " +
            $"one move/index p50/p95={Percentile(maintenance, .50):F3}/{Percentile(maintenance, .95):F3}ms, " +
            $"update p50/p95={Percentile(updates, .50):F3}/{Percentile(updates, .95):F3}ms, " +
            $"{positionQueries} position queries p50/p95={Percentile(queries, .50):F3}/{Percentile(queries, .95):F3}ms, " +
            $"median allocated full/move/update/queries={Percentile(buildBytes, .50):F0}/" +
            $"{Percentile(maintenanceBytes, .50):F0}/{Percentile(updateBytes, .50):F0}/" +
            $"{Percentile(queryBytes, .50):F0} bytes, " +
            $"full rule/object evaluations={ruleEvaluations}/{objectEvaluations}.");
    }

    private void MeasureGrid(string name, int count, double spread, int moves, double radius, bool wrapped)
    {
        const int warmups = 2, samples = 12, queries = 32;
        var random = new Random(19820);
        MapSpace space = wrapped ? MapSpace.Toroidal(103, 97) : MapSpace.Planar;
        Vector2D NextPosition() => wrapped
            ? new(random.NextDouble() * 103, random.NextDouble() * 97)
            : new(100 + random.NextDouble() * spread, 100 + random.NextDouble() * spread);
        var grid = new SpatialGrid<int>(space);
        Vector2D[] positions = Enumerable.Range(0, count).Select(_ => NextPosition()).ToArray();
        Vector2D[] original = positions.ToArray();
        for (int id = 0; id < count; id++) grid.Add(id, positions[id]);
        Vector2D[] centers = Enumerable.Range(0, queries).Select(_ => NextPosition()).ToArray();
        var indexedTimes = new List<double>(samples);
        var scanTimes = new List<double>(samples);
        var maintenanceTimes = new List<double>(samples);
        var indexedBytes = new List<long>(samples);
        var scanBytes = new List<long>(samples);
        long cells = 0, entries = 0, candidates = 0;

        for (int iteration = -warmups; iteration < samples; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            for (int id = 0; id < moves; id++)
            {
                double shift = iteration % 2 == 0 ? 1 : -1;
                positions[id] = new(original[id].X + shift, original[id].Y);
                grid.Update(id, positions[id]);
            }
            double maintained = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            var actual = new int[queries][];
            long visited = 0, entriesExamined = 0, examined = 0;
            for (int query = 0; query < queries; query++)
            {
                Vector2D center = centers[query];
                var minimum = new Vector2D(center.X - radius, center.Y - radius);
                var maximum = new Vector2D(center.X + radius, center.Y + radius);
                IReadOnlyList<int> nearby = grid.QueryBox(minimum, maximum, out var statistics);
                visited += statistics.CellsVisited;
                entriesExamined += statistics.EntriesExamined;
                examined += statistics.Candidates;
                actual[query] = nearby.Where(id => space.Distance(center, positions[id]) <= radius).ToArray();
            }
            double indexed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long indexedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            var expected = new int[queries][];
            for (int query = 0; query < queries; query++)
            {
                Vector2D center = centers[query];
                expected[query] = Enumerable.Range(0, count)
                    .Where(id => space.Distance(center, positions[id]) <= radius).ToArray();
            }
            double scanned = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long scanAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;

            for (int query = 0; query < queries; query++)
                CollectionAssert.AreEqual(expected[query], actual[query], $"{name}, query={query}");
            if (iteration < 0) continue;
            indexedTimes.Add(indexed);
            scanTimes.Add(scanned);
            maintenanceTimes.Add(maintained);
            indexedBytes.Add(indexedAllocation);
            scanBytes.Add(scanAllocation);
            cells = visited;
            entries = entriesExamined;
            candidates = examined;
        }

        TestContext.WriteLine(
            $"grid {name}: units={count}, moves={moves}, queries={queries}, samples={samples}, " +
            $"move/index p50/p95={Percentile(maintenanceTimes, .50):F3}/{Percentile(maintenanceTimes, .95):F3}ms, " +
            $"indexed queries p50/p95={Percentile(indexedTimes, .50):F3}/{Percentile(indexedTimes, .95):F3}ms, " +
            $"direct scan p50/p95={Percentile(scanTimes, .50):F3}/{Percentile(scanTimes, .95):F3}ms, " +
            $"median allocated indexed/scan={Percentile(indexedBytes, .50):F0}/{Percentile(scanBytes, .50):F0} bytes, " +
            $"cells visited={cells}, entries scanned={entries}, candidates/exact checks={candidates}/{candidates}, " +
            $"direct checks={count * queries}.");
    }

    private void MeasurePipeline(string name, double spread, double vision, int moveCount, bool unboundedRule)
    {
        const int warmups = 2, samples = 12, probesPerMap = 16;
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var random = new Random(40961);
        var maps = new[] { new MapRuntime(5000, 5000, id: "first"), new MapRuntime(5000, 5000, id: "second") };
        Vector2D NextPosition() => new(100 + random.NextDouble() * spread, 100 + random.NextDouble() * spread);
        var observers = new List<MapVisibilityObserver>();
        var targets = new List<MapVisibilityTarget>();
        var leases = new List<IDisposable>();
        foreach (MapRuntime map in maps)
        {
            var mapObservers = new List<MapObject>();
            var mapTargets = new List<MapObject>();
            for (int index = 0; index < 12; index++)
            {
                var body = new MapObject($"{map.Id}-viewer-{index}", NextPosition(), receiver);
                map.AddObject(body);
                mapObservers.Add(body);
                observers.Add(new(index % 2 == 0 ? "blue" : "red", body, vision));
            }
            for (int index = 0; index < 80; index++)
            {
                var body = new MapObject($"{map.Id}-target-{index}", NextPosition(), receiver,
                    kind: index % 4 == 0 ? MapObjectKind.Projectile : MapObjectKind.Player);
                map.AddObject(body);
                mapTargets.Add(body);
                targets.Add(new(body, index % 3));
            }
            if (unboundedRule) leases.Add(map.RegisterVisibilityRule(new BenchmarkUnboundedRule()));
            leases.Add(map.RegisterVisibilityRule(new BenchmarkPairRule(mapObservers[0].Id, mapTargets[^1].Id)));
        }
        var scene = new MapVisibilityScene(["blue", "red"], observers, targets);
        var manager = new MapVisibilityManager(MapVisibilityArea.Default);
        var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        MapObject[] movers = observers.Take(Math.Min(4, moveCount)).Select(observer => observer.Body)
            .Concat(targets.Take(moveCount - Math.Min(4, moveCount)).Select(target => target.Body)).ToArray();
        Vector2D[] origins = movers.Select(body => body.Position).ToArray();
        Vector2D[][] probes = maps.Select(_ => Enumerable.Range(0, probesPerMap).Select(_ => NextPosition()).ToArray()).ToArray();
        var indexedTimes = new List<double>(samples);
        var directTimes = new List<double>(samples);
        var maintenanceTimes = new List<double>(samples);
        var indexedBytes = new List<long>(samples);
        var directBytes = new List<long>(samples);
        int ruleEvaluations = 0, objectEvaluations = 0;

        for (int iteration = -warmups; iteration < samples; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < movers.Length; index++)
            {
                double shift = iteration % 2 == 0 ? 1 : -1;
                movers[index].Position = new(origins[index].X + shift, origins[index].Y);
            }
            double maintained = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            snapshot = snapshot.Update(scene);
            var actualUnits = new List<(string Team, MapObject Body)>();
            var actualPositions = new List<bool>();
            foreach (string team in scene.Teams)
            for (int mapIndex = 0; mapIndex < maps.Length; mapIndex++)
            {
                actualUnits.AddRange(snapshot.GetVisibleObjects(team, maps[mapIndex]).Select(body => (team, body)));
                foreach (Vector2D position in probes[mapIndex])
                    actualPositions.Add(snapshot.IsPositionVisible(team, maps[mapIndex], position));
            }
            double indexed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long indexedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
            ruleEvaluations = snapshot.RuleEvaluations;
            objectEvaluations = snapshot.ObjectEvaluations;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            var expectedUnits = new List<(string Team, MapObject Body)>();
            var expectedPositions = new List<bool>();
            foreach (string team in scene.Teams)
            for (int mapIndex = 0; mapIndex < maps.Length; mapIndex++)
            {
                MapRuntime map = maps[mapIndex];
                foreach (MapVisibilityTarget target in targets.Where(target => target.Body.MapRuntime == map))
                    if (manager.IsVisible(team, target.Body, target.Stealth, observers, map.VisibilityRules))
                        expectedUnits.Add((team, target.Body));
                foreach (Vector2D position in probes[mapIndex])
                    expectedPositions.Add(manager.IsPositionVisible(team, map, position, 0, observers));
            }
            double direct = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long directAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;

            CollectionAssert.AreEqual(expectedUnits, actualUnits, name);
            CollectionAssert.AreEqual(expectedPositions, actualPositions, name);
            if (iteration < 0) continue;
            indexedTimes.Add(indexed);
            directTimes.Add(direct);
            maintenanceTimes.Add(maintained);
            indexedBytes.Add(indexedAllocation);
            directBytes.Add(directAllocation);
        }

        foreach (IDisposable lease in leases) lease.Dispose();
        TestContext.WriteLine(
            $"pipeline {name}: maps=2, teams=2, observers=24, targets/projectiles=160, moves={moveCount}, " +
            $"position queries={2 * maps.Length * probesPerMap}, samples={samples}, " +
            $"move/index p50/p95={Percentile(maintenanceTimes, .50):F3}/{Percentile(maintenanceTimes, .95):F3}ms, " +
            $"update/visible-sets/queries p50/p95={Percentile(indexedTimes, .50):F3}/{Percentile(indexedTimes, .95):F3}ms, " +
            $"direct full scan p50/p95={Percentile(directTimes, .50):F3}/{Percentile(directTimes, .95):F3}ms, " +
            $"median allocated indexed/direct={Percentile(indexedBytes, .50):F0}/{Percentile(directBytes, .50):F0} bytes, " +
            $"last update rule/object evaluations={ruleEvaluations}/{objectEvaluations}.");
    }

    private sealed class BenchmarkUnboundedRule : IMapVisibilityRule
    {
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) => null;
    }

    private sealed class BenchmarkPairRule(string viewerId, string targetId) : IScopedMapVisibilityRule
    {
        public IReadOnlyCollection<(string ViewerId, string TargetId)> GetAffectedPairs() => [(viewerId, targetId)];

        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) =>
            viewer.Id == viewerId && target.Id == targetId ? true : null;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }

    private static double Percentile(IEnumerable<long> values, double percentile) =>
        Percentile(values.Select(value => (double)value), percentile);
}
