using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class VisibilitySpatialDifferentialTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void IncrementalSnapshotsMatchDirectRulesOnBothMapTopologies()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var maps = new[]
        {
            new MapRuntime(500, 500),
            new MapRuntime(new MapDefinition("wrapped", "wrapped", 103, 97, "Overworld", "Torus")),
        };
        var random = new Random(9361);
        var observers = new List<MapVisibilityObserver>();
        var targets = new List<MapVisibilityTarget>();
        foreach (MapRuntime map in maps)
        {
            for (int index = 0; index < 12; index++)
            {
                var body = new MapObject($"{map.Id}-viewer-{index}", Position(map, random), receiver);
                map.AddObject(body);
                observers.Add(new(index % 2 == 0 ? "blue" : "red", body, 15 + index * 3));
            }
            for (int index = 0; index < 30; index++)
            {
                var body = new MapObject($"{map.Id}-target-{index}", Position(map, random), receiver);
                map.AddObject(body);
                targets.Add(new(body, index % 8));
            }
        }
        var unbounded = new PositionRule();
        using IDisposable global = maps[0].RegisterVisibilityRule(unbounded);
        var scoped = new PairRule(observers[12].Body.Id, targets[30].Body.Id);
        using IDisposable pair = maps[1].RegisterVisibilityRule(scoped);
        var scene = new MapVisibilityScene(["blue", "red"], observers, targets);
        var manager = new MapVisibilityManager(MapVisibilityArea.Default);
        var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        int comparisons = 0;

        for (int frame = 0; frame < 36; frame++)
        {
            Check(snapshot);
            MapVisibilitySnapshot old = snapshot;
            bool oldVisibility = old.IsVisible("blue", targets[0].Body);
            Vector2D oldProbe = new(15, 15);
            bool oldPosition = old.IsPositionVisible("blue", maps[0], oldProbe);
            if (frame % 3 == 0)
            {
                int index = random.Next(observers.Count);
                MapVisibilityObserver observer = observers[index];
                observer.Body.Position = Position(observer.Body.MapRuntime!, random);
                observers[index] = observer with { VisionRange = random.Next(0, 100), IsAlive = frame % 4 != 0 };
            }
            else
            {
                int index = random.Next(targets.Count);
                MapVisibilityTarget target = targets[index];
                target.Body.Position = Position(target.Body.MapRuntime!, random);
                targets[index] = target with { Stealth = random.Next(0, 30) };
            }
            snapshot = snapshot.Update(scene);
            Assert.AreEqual(oldVisibility, old.IsVisible("blue", targets[0].Body));
            Assert.AreEqual(oldPosition, old.IsPositionVisible("blue", maps[0], oldProbe));
        }
        Check(snapshot);
        TestContext.WriteLine($"{comparisons} object and position decisions matched direct evaluation over 37 frames.");

        void Check(MapVisibilitySnapshot current)
        {
            foreach (string team in scene.Teams)
            {
                foreach (MapVisibilityTarget target in targets)
                {
                    bool expected = manager.IsVisible(team, target.Body, target.Stealth, observers,
                        target.Body.MapRuntime!.VisibilityRules);
                    Assert.AreEqual(expected, current.IsVisible(team, target.Body),
                        $"team={team}, target={target.Body.Id}");
                    comparisons++;
                }
                foreach (MapRuntime map in maps)
                for (int sample = 0; sample < 5; sample++)
                {
                    Vector2D position = Position(map, random);
                    bool expected = manager.IsPositionVisible(team, map, position, 4, observers);
                    Assert.AreEqual(expected, current.IsPositionVisible(team, map, position, 4));
                    comparisons++;
                }
            }
        }
    }

    [TestMethod]
    public void ScopedRemoteRevealAndUnboundedOverrideRetainPrecedence()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(1000, 1000);
        var viewer = new MapObject("viewer", new(10, 10), receiver);
        var near = new MapObject("near", new(11, 10), receiver);
        var remote = new MapObject("remote", new(900, 900), receiver);
        foreach (MapObject body in new[] { viewer, near, remote }) map.AddObject(body);
        var scene = new MapVisibilityScene(["blue"], [new("blue", viewer, 1)], [new(near, 0), new(remote, 0)]);
        using IDisposable denied = map.RegisterVisibilityRule(new ConstantRule(false));
        using IDisposable reveal = map.RegisterVisibilityRule(new PairRule(viewer.Id, remote.Id));
        var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        Assert.IsTrue(snapshot.IsVisible("blue", near));
        Assert.IsTrue(snapshot.IsVisible("blue", remote));
        Assert.AreEqual(2, snapshot.RuleEvaluations);
        reveal.Dispose();
        var deniedSnapshot = snapshot.Update(scene, refreshRules: true);
        Assert.IsFalse(deniedSnapshot.IsVisible("blue", remote));
        Assert.IsTrue(snapshot.IsVisible("blue", remote));
        TestContext.WriteLine("The guaranteed rectangle wins over denial; a later scoped rule reveals only its remote pair.");
    }

    [TestMethod]
    public void IndexedSweptIntervalsMatchAllObserversAcrossIncrementalFrames()
    {
        int comparisons = 0;
        foreach (bool wrapped in new[] { false, true })
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            var map = wrapped
                ? new MapRuntime(new MapDefinition("world", "world", 103, 97, "Overworld", "Torus"))
                : new MapRuntime(500, 500);
            var random = new Random(wrapped ? 903 : 904);
            var observers = Enumerable.Range(0, 24).Select(index =>
            {
                var body = new MapObject($"viewer-{index}", Position(map, random), receiver);
                map.AddObject(body);
                return new MapVisibilityObserver(index % 2 == 0 ? "blue" : "red", body, 5 + random.NextDouble() * 50);
            }).ToArray();
            var targets = Enumerable.Range(0, 40).Select(index =>
            {
                var body = new MapObject($"target-{index}", Position(map, random), receiver);
                map.AddObject(body);
                return new MapVisibilityTarget(body, random.NextDouble() * 15);
            }).ToArray();
            using IDisposable lease = map.RegisterVisibilityRule(new PairRule(observers[0].Body.Id, targets[0].Body.Id));
            var area = new MapVisibilityArea(12, 8 * Math.Sqrt(2), new(3, 4));
            var manager = new MapVisibilityManager(area);
            var scene = new MapVisibilityScene(["blue", "red"], observers, targets);
            var snapshot = new MapVisibilitySnapshot(area, scene);

            for (int frame = 0; frame < 100; frame++)
            {
                MapVisibilityTarget target = targets[frame % targets.Length];
                MapObject moving = frame % 2 == 0
                    ? target.Body : observers[random.Next(observers.Length)].Body;
                string team = frame % 4 < 2 ? "blue" : "red";
                Vector2D from = moving.Position;
                Vector2D to = Position(map, random);
                IReadOnlyList<MapVisibilityInterval> expected = manager.TraceVisibility(team, target.Body,
                    target.Stealth, observers, moving, from, to, map.VisibilityRules);
                IReadOnlyList<MapVisibilityInterval> actual = snapshot.TraceVisibility(team, target.Body, moving, from, to);
                EqualIntervals(expected, actual, $"wrapped={wrapped}, frame={frame}");

                moving.Position = to;
                EqualIntervals(actual, snapshot.TraceVisibility(team, target.Body, moving, from, to),
                    $"old snapshot, wrapped={wrapped}, frame={frame}");
                snapshot = snapshot.Update(scene);
                comparisons++;
            }
        }
        TestContext.WriteLine($"{comparisons} indexed swept paths matched full observer scans and retained old snapshots.");
    }

    private static void EqualIntervals(IReadOnlyList<MapVisibilityInterval> expected,
        IReadOnlyList<MapVisibilityInterval> actual, string message)
    {
        Assert.HasCount(expected.Count, actual, message);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index].Start, actual[index].Start, 1e-12, message);
            Assert.AreEqual(expected[index].End, actual[index].End, 1e-12, message);
        }
    }

    private static Vector2D Position(MapRuntime map, Random random) =>
        new(random.NextDouble() * map.Width, random.NextDouble() * map.Height);

    private sealed class PositionRule : IMapVisibilityRule
    {
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) =>
            viewer.Id.EndsWith("viewer-0", StringComparison.Ordinal) &&
            target.Id.EndsWith("target-0", StringComparison.Ordinal)
                ? viewer.Position.X > 250 : null;
    }

    private sealed class ConstantRule(bool result) : IMapVisibilityRule
    {
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) => result;
    }

    private sealed class PairRule(string viewerId, string targetId) : IScopedMapVisibilityRule
    {
        public IReadOnlyCollection<(string ViewerId, string TargetId)> GetAffectedPairs() => [(viewerId, targetId)];

        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) =>
            viewer.Id == viewerId && target.Id == targetId ? true : null;
    }
}
