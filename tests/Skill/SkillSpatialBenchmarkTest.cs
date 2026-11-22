using System.Diagnostics;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SkillSpatialBenchmarkTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void RepresentativeCircleAndLineQueriesIncludeMovementMaintenance()
    {
        Measure("small", 24, 200, 1, 25, 100, 5, wrapped: false);
        Measure("sparse-few", 800, 4000, 1, 25, 100, 5, wrapped: false);
        Measure("sparse-many", 800, 4000, 20, 25, 100, 5, wrapped: false);
        Measure("dense-many", 800, 120, 20, 25, 100, 5, wrapped: false);
        Measure("long-range", 800, 4000, 20, 4000, 4000, 2000, wrapped: false);
        Measure("wrapped", 800, 103, 20, 25, 100, 5, wrapped: true);
    }

    private void Measure(string name, int count, double spread, int moves, double radius,
        double lineLength, double halfWidth, bool wrapped)
    {
        const int warmups = 2, samples = 12, queries = 32;
        var context = new Context();
        context.Logger.Output = null;
        var registry = new UnitSpatialRegistry();
        var map = wrapped
            ? new MapRuntime(new MapDefinition("world", "world", 103, 97, "Overworld", "Torus"))
            : new MapRuntime(5000, 5000);
        var random = new Random(33117);
        Vector2D NextPosition() => wrapped
            ? new(random.NextDouble() * 103, random.NextDouble() * 97)
            : new(100 + random.NextDouble() * spread, 100 + random.NextDouble() * spread);
        Unit[] units = Enumerable.Range(0, count).Select(_ => CreateUnit(context, NextPosition())).ToArray();
        foreach (Unit unit in units)
        {
            registry.Register(unit);
            map.AddObject(unit.MapObject!);
        }
        Vector2D[] original = units.Select(unit => unit.MapObject!.Position).ToArray();
        Vector2D[] centers = Enumerable.Range(0, queries).Select(_ => NextPosition()).ToArray();
        var maintenanceTimes = new List<double>(samples);
        var indexedTimes = new List<double>(samples);
        var directTimes = new List<double>(samples);
        var maintenanceBytes = new List<long>(samples);
        var indexedBytes = new List<long>(samples);
        var directBytes = new List<long>(samples);
        long circleCandidates = 0, lineCandidates = 0;
        Vector2D direction = new(3, 4);
        const double nx = .6, ny = .8;

        for (int iteration = -warmups; iteration < samples; iteration++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < moves; index++)
            {
                double shift = iteration % 2 == 0 ? 1 : -1;
                units[index].MapObject!.Position = new(original[index].X + shift, original[index].Y);
            }
            double maintenance = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long moveAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            var actualCircles = new Unit[queries][];
            var actualLines = new Unit[queries][];
            for (int query = 0; query < queries; query++)
            {
                actualCircles[query] = registry.QueryInRadius(centers[query], radius, map: map).ToArray();
                actualLines[query] = registry.QueryInLine(centers[query], direction, lineLength, halfWidth, map: map).ToArray();
            }
            double indexed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long indexedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            var expectedCircles = new Unit[queries][];
            var expectedLines = new Unit[queries][];
            for (int query = 0; query < queries; query++)
            {
                Vector2D center = centers[query];
                expectedCircles[query] = units.Where(unit => unit.IsAlive &&
                    UnitSpatialRegistry.ObjectIn(unit, map) is { IsActive: true, IsInteractable: true, IsSelectable: true } body &&
                    body.Position.DistanceTo(center) <= radius).ToArray();
                expectedLines[query] = units.Where(unit =>
                {
                    if (!unit.IsAlive || UnitSpatialRegistry.ObjectIn(unit, map) is not
                        { IsActive: true, IsInteractable: true, IsSelectable: true } body) return false;
                    Vector2D point = body.Position;
                    double dx = point.X - center.X, dy = point.Y - center.Y;
                    double along = dx * nx + dy * ny;
                    return along >= 0 && along <= lineLength && Math.Abs(dx * ny - dy * nx) <= halfWidth;
                }).ToArray();
            }
            double direct = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long directAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;

            for (int query = 0; query < queries; query++)
            {
                CollectionAssert.AreEqual(expectedCircles[query], actualCircles[query], $"{name}, circle={query}");
                CollectionAssert.AreEqual(expectedLines[query], actualLines[query], $"{name}, line={query}");
            }
            if (iteration < 0) continue;
            maintenanceTimes.Add(maintenance);
            indexedTimes.Add(indexed);
            directTimes.Add(direct);
            maintenanceBytes.Add(moveAllocation);
            indexedBytes.Add(indexedAllocation);
            directBytes.Add(directAllocation);
            circleCandidates = 0;
            lineCandidates = 0;
            foreach (Vector2D center in centers)
            {
                circleCandidates += map.QuerySpatialCandidates(
                    new(center.X - radius, center.Y - radius), new(center.X + radius, center.Y + radius)).Count;
                lineCandidates += map.QuerySpatialCandidates(
                    new(center.X - halfWidth, center.Y - halfWidth),
                    new(center.X + nx * lineLength + halfWidth, center.Y + ny * lineLength + halfWidth)).Count;
            }
        }

        TestContext.WriteLine(
            $"skills {name}: units={count}, moves={moves}, circle/line queries={queries}/{queries}, samples={samples}, " +
            $"move/index p50/p95={Percentile(maintenanceTimes, .50):F3}/{Percentile(maintenanceTimes, .95):F3}ms, " +
            $"indexed queries p50/p95={Percentile(indexedTimes, .50):F3}/{Percentile(indexedTimes, .95):F3}ms, " +
            $"direct queries p50/p95={Percentile(directTimes, .50):F3}/{Percentile(directTimes, .95):F3}ms, " +
            $"median allocated move/indexed/direct={Percentile(maintenanceBytes, .50):F0}/" +
            $"{Percentile(indexedBytes, .50):F0}/{Percentile(directBytes, .50):F0} bytes, " +
            $"circle/line candidates={circleCandidates}/{lineCandidates}, direct checks={2 * count * queries}.");
    }

    private static Unit CreateUnit(Context context, Vector2D position)
    {
        var unit = new Unit
        {
            Name = "benchmark", ArchetypeId = "benchmark",
            Property = new UnitProperty { LifePoint = 100, LifePointMax = 100 },
            Abilities = new UnitAbilityManager(), EventReceiver = context.Factory.NewEventReceiver()
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler(), new AbilityLifecycleHandler());
        context.AddEventReceiver(unit.EventReceiver);
        unit.Initialize();
        unit.MapObjects.Initialize(new MapObject(unit.MapObjects.UnitId, position, unit.EventReceiver));
        return unit;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }

    private static double Percentile(IEnumerable<long> values, double percentile) =>
        Percentile(values.Select(value => (double)value), percentile);
}
