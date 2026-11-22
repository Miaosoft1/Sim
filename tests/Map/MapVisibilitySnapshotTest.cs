using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using System.Diagnostics;

namespace Miaosoft.Sim.Combat.Map.Tests;

[TestClass]
public sealed class MapVisibilitySnapshotTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void QueriesDoNotReadLiveBodiesOrReevaluateRules()
    {
        var context = new Context();
        var map = new MapRuntime(500, 500);
        var viewer = new MapObject("viewer", new(100, 100), context.Factory.NewEventReceiver());
        var target = new MapObject("target", new(140, 100), context.Factory.NewEventReceiver());
        map.AddObject(viewer);
        map.AddObject(target);
        var rule = new CountingRule();
        using var lease = map.RegisterVisibilityRule(rule);
        var scene = new MapVisibilityScene(["blue"], [new("blue", viewer, 50)], [new(target, 0)]);
        var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        int evaluations = rule.Count;
        viewer.Position = new(300, 300);
        target.Position = new(400, 400);
        viewer.IsActive = false;
        for (int i = 0; i < 10; i++)
        {
            Assert.IsTrue(snapshot.IsVisible("blue", target));
            Assert.IsTrue(snapshot.IsPositionVisible("blue", map, new(140, 100)));
            Assert.IsNotEmpty(snapshot.TraceVisibility("blue", target, target, new(140, 100), new(145, 100)));
            CollectionAssert.AreEqual(new[] { target }, map.GetVisibleObjects("blue", snapshot).ToArray());
        }
        Assert.AreEqual(evaluations, rule.Count);
        int additionalEvaluations = rule.Count - evaluations;
        var updated = snapshot.Update(scene);
        Assert.IsFalse(updated.IsVisible("blue", target));
        Assert.IsTrue(snapshot.IsVisible("blue", target));
        TestContext.WriteLine($"Repeated snapshot queries: 40, additional rule evaluations: {additionalEvaluations}.");
    }

    [TestMethod]
    public void LocalTargetUpdateReusesUnchangedVisibilityResults()
    {
        var context = new Context();
        var map = new MapRuntime(500, 500);
        var receiver = context.Factory.NewEventReceiver();
        var viewer = new MapObject("viewer", new(100, 100), receiver);
        var first = new MapObject("first", new(110, 100), receiver);
        var second = new MapObject("second", new(120, 100), receiver);
        foreach (MapObject body in new[] { viewer, first, second }) map.AddObject(body);
        var scene = new MapVisibilityScene(["blue"], [new("blue", viewer, 50)],
            [new(first, 0), new(second, 0)]);
        var before = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        var unchanged = before.Update(scene);
        Assert.AreEqual(0, unchanged.ObjectEvaluations);
        Assert.AreEqual(0, unchanged.RuleEvaluations);
        first.Position = new(200, 200);
        var after = before.Update(scene);
        Assert.AreEqual(1, after.ObjectEvaluations);
        Assert.IsFalse(after.IsVisible("blue", first));
        Assert.IsTrue(after.IsVisible("blue", second));
        Assert.IsTrue(before.IsVisible("blue", first));
        TestContext.WriteLine($"Changed target evaluations: {after.ObjectEvaluations}; unchanged update: {unchanged.ObjectEvaluations}.");
    }

    [TestMethod]
    public void RuleChangesRequireExplicitRefreshAndKeepOldSnapshotStable()
    {
        var context = new Context();
        var map = new MapRuntime(500, 500);
        var receiver = context.Factory.NewEventReceiver();
        var viewer = new MapObject("viewer", new(100, 100), receiver);
        var target = new MapObject("target", new(400, 400), receiver);
        map.AddObject(viewer);
        map.AddObject(target);
        var scene = new MapVisibilityScene(["blue"], [new("blue", viewer, 50)], [new(target, 0)]);
        var before = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        using var lease = map.RegisterVisibilityRule(new CountingRule { Result = true });
        var revealed = before.Update(scene, refreshRules: true);
        Assert.IsTrue(revealed.IsVisible("blue", target));
        Assert.IsFalse(before.IsVisible("blue", target));
        lease.Dispose();
        Assert.IsTrue(revealed.IsVisible("blue", target));
        Assert.IsFalse(revealed.Update(scene, refreshRules: true).IsVisible("blue", target));
    }

    [TestMethod]
    public void IncrementalChangesEvaluateOnlyAffectedObserverTargetPairs()
    {
        const int observerCount = 40, targetCount = 80;
        var context = new Context();
        var map = new MapRuntime(1000, 1000);
        var receiver = context.Factory.NewEventReceiver();
        var observers = Enumerable.Range(0, observerCount)
            .Select(i => new MapObject($"observer-{i}", new(100 + i, 100), receiver)).ToArray();
        var targets = Enumerable.Range(0, targetCount)
            .Select(i => new MapObject($"target-{i}", new(200 + i, 100), receiver)).ToArray();
        foreach (MapObject body in observers.Concat(targets)) map.AddObject(body);
        var rule = new CountingRule();
        using var lease = map.RegisterVisibilityRule(rule);
        var scene = new MapVisibilityScene(["blue"],
            observers.Select(body => new MapVisibilityObserver("blue", body, 200)).ToArray(),
            targets.Select(body => new MapVisibilityTarget(body, 0)).ToArray());
        var original = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        Assert.AreEqual(observerCount * targetCount, original.RuleEvaluations);

        targets[0].Position = new(700, 700);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var targetUpdate = original.Update(scene);
        stopwatch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.AreEqual(observerCount, targetUpdate.RuleEvaluations);
        Assert.AreEqual(1, targetUpdate.ObjectEvaluations);
        Assert.IsTrue(original.IsVisible("blue", targets[0]));
        Assert.IsFalse(targetUpdate.IsVisible("blue", targets[0]));

        observers[0].Position = new(101, 101);
        var observerUpdate = targetUpdate.Update(scene);
        Assert.AreEqual(targetCount, observerUpdate.RuleEvaluations);
        Assert.AreEqual(targetCount - 1, observerUpdate.ObjectEvaluations);
        var rebuilt = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        foreach (MapObject target in targets)
            Assert.AreEqual(rebuilt.IsVisible("blue", target), observerUpdate.IsVisible("blue", target));
        TestContext.WriteLine($"A target update evaluated {targetUpdate.RuleEvaluations}/{original.RuleEvaluations} pairs, " +
            $"allocated {allocated} bytes, and took {stopwatch.Elapsed}; one observer update evaluated {observerUpdate.RuleEvaluations} pairs.");
    }

    [TestMethod]
    public void MultiTeamMultiMapIncrementalWorkMatchesFullRebuild()
    {
        const int observersPerTeamPerMap = 10, targetsPerMap = 40, iterations = 8;
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var maps = new[] { new MapRuntime(2000, 2000), new MapRuntime(2000, 2000) };
        var teams = new[] { "blue", "red" };
        var observers = new List<MapVisibilityObserver>();
        var targets = new List<MapVisibilityTarget>();
        var rule = new CountingRule();
        using var firstLease = maps[0].RegisterVisibilityRule(rule);
        using var secondLease = maps[1].RegisterVisibilityRule(rule);
        for (int mapIndex = 0; mapIndex < maps.Length; mapIndex++)
        {
            foreach (string team in teams)
            for (int i = 0; i < observersPerTeamPerMap; i++)
            {
                var body = new MapObject($"{mapIndex}-{team}-observer-{i}",
                    new(100 + i * 3, team == "blue" ? 100 : 120), receiver);
                maps[mapIndex].AddObject(body);
                observers.Add(new MapVisibilityObserver(team, body, 200));
            }
            for (int i = 0; i < targetsPerMap; i++)
            {
                var body = new MapObject($"{mapIndex}-target-{i}", new(150 + i * 3, 110), receiver);
                maps[mapIndex].AddObject(body);
                targets.Add(new MapVisibilityTarget(body, 0));
            }
        }
        var scene = new MapVisibilityScene(teams, observers, targets);
        var original = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        targets[0].Body.Position = new(900, 900);
        _ = original.Update(scene);
        _ = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        MapVisibilitySnapshot incremental = original;
        for (int i = 0; i < iterations; i++) incremental = original.Update(scene);
        stopwatch.Stop();
        long incrementalBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TimeSpan incrementalTime = stopwatch.Elapsed;

        allocated = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        MapVisibilitySnapshot rebuilt = original;
        for (int i = 0; i < iterations; i++) rebuilt = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        stopwatch.Stop();
        long rebuildBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.AreEqual(20, incremental.RuleEvaluations);
        Assert.AreEqual(2, incremental.ObjectEvaluations);
        Assert.AreEqual(1600, rebuilt.RuleEvaluations);
        Assert.AreEqual(160, rebuilt.ObjectEvaluations);
        foreach (string team in teams)
        foreach (MapVisibilityTarget target in targets)
            Assert.AreEqual(rebuilt.IsVisible(team, target.Body), incremental.IsVisible(team, target.Body));
        Assert.IsTrue(original.IsVisible("blue", targets[0].Body));
        Assert.IsFalse(incremental.IsVisible("blue", targets[0].Body));
        TestContext.WriteLine($"Two teams, two maps, {iterations} repetitions: incremental " +
            $"{incremental.RuleEvaluations} rule pairs/{incremental.ObjectEvaluations} object evaluations, " +
            $"{incrementalBytes} bytes/{incrementalTime}; full rebuild " +
            $"{rebuilt.RuleEvaluations} rule pairs/{rebuilt.ObjectEvaluations} object evaluations, " +
            $"{rebuildBytes} bytes/{stopwatch.Elapsed}.");
    }

    private sealed class CountingRule : IMapVisibilityRule
    {
        public int Count { get; private set; }
        public bool? Result { get; init; }
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target)
        {
            Count++;
            return Result;
        }
    }
}
