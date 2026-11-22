using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class TrackingSkillTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TrackingSkill_RevealsTrackedEnemyToTeamUntilEffectEnds()
    {
        Context context = new();
        EventReceiver receiver = context.Factory.NewEventReceiver();
        MapRuntime map = new();
        MapObject caster = new("caster", new Vector2D(1, 1), receiver, teamId: "blue");
        MapObject teammate = new("teammate", new Vector2D(2, 2), receiver, teamId: "blue");
        MapObject enemy = new("enemy", new Vector2D(70, 80), receiver, teamId: "red");
        map.AddObject(caster);
        map.AddObject(teammate);
        map.AddObject(enemy);
        TrackingSkill skill = new();

        Assert.IsFalse(map.GetVisibleObjects("blue", Scene(map, caster, teammate)).Any(item => item.Id == enemy.Id));
        using (skill.Apply(caster, enemy))
        {
            MapObject trackedEnemy = map.GetVisibleObjects("blue", Scene(map, caster, teammate))
                .Single(item => item.Id == enemy.Id);
            Assert.AreEqual(new Vector2D(70, 80), trackedEnemy.Position);
            Assert.IsTrue(map.GetVisibleObjects("blue", Scene(map, caster, teammate)).Any(item => item.Id == enemy.Id));
        }

        Assert.IsFalse(map.GetVisibleObjects("blue", Scene(map, caster, teammate)).Any(item => item.Id == enemy.Id));
    }

    [TestMethod]
    public void TrackingSkillFollowsRepresentationsAcrossMapsUntilEveryLeaseEnds()
    {
        Context context = new();
        EventReceiver receiver = context.Factory.NewEventReceiver();
        MapRuntime initial = new(500, 500);
        MapRuntime remote = new(500, 500);
        MapObject caster = new("caster", new Vector2D(100, 100), receiver, teamId: "blue");
        MapObject target = new("target", new Vector2D(170, 100), receiver, teamId: "red");
        var casterObjects = new UnitMapObjectManager(caster.Id);
        var targetObjects = new UnitMapObjectManager(target.Id);
        casterObjects.Initialize(caster);
        targetObjects.Initialize(target);
        initial.AddObject(caster);
        initial.AddObject(target);
        var skill = new TrackingSkill();
        using IDisposable first = skill.Apply(caster, target);
        using IDisposable second = skill.Apply(caster, target);

        MapObject remoteCaster = casterObjects.Enter(remote, new Vector2D(100, 100), TimeSpan.Zero,
            keepSourceVisible: false);
        Assert.IsFalse(remote.GetVisibleObjects("blue", Scene(remote, remoteCaster)).Any(body => body.Id == target.Id));
        MapObject remoteTarget = targetObjects.Enter(remote, new Vector2D(170, 100), TimeSpan.Zero,
            keepSourceVisible: false);
        MapObject teammate = new("teammate", new Vector2D(101, 100), receiver, teamId: "blue");
        remote.AddObject(teammate);
        Assert.IsTrue(remote.GetVisibleObjects("blue", Scene(remote, remoteCaster, teammate)).Contains(remoteTarget));

        first.Dispose();
        Assert.IsTrue(remote.GetVisibleObjects("blue", Scene(remote, remoteCaster, teammate)).Contains(remoteTarget));
        second.Dispose();
        Assert.IsFalse(remote.GetVisibleObjects("blue", Scene(remote, remoteCaster, teammate)).Contains(remoteTarget));
    }

    [TestMethod]
    public void TrackingOnlyEvaluatesItsRemotePair()
    {
        var context = new Context();
        var receiver = context.Factory.NewEventReceiver();
        var map = new MapRuntime(2000, 2000);
        var caster = new MapObject("caster", new(10, 10), receiver);
        var target = new MapObject("target", new(1800, 1800), receiver);
        map.AddObject(caster);
        map.AddObject(target);
        var observers = new List<MapVisibilityObserver> { new("blue", caster, 10) };
        for (int index = 0; index < 20; index++)
        {
            var other = new MapObject($"other-{index}", new(100 + index * 30, 100), receiver);
            map.AddObject(other);
            observers.Add(new("blue", other, 10));
        }
        using IDisposable lease = new TrackingSkill().Apply(caster, target);
        var scene = new MapVisibilityScene(["blue"], observers,
            map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray());
        var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
        Assert.IsTrue(snapshot.IsVisible("blue", target));
        Assert.AreEqual(1, snapshot.RuleEvaluations);
        TestContext.WriteLine("A remote tracking reveal evaluated one scoped pair rather than every observer/target pair.");
    }

    private static MapVisibilitySnapshot Scene(MapRuntime map, params MapObject[] observers) => new(
        MapVisibilityArea.Default, new MapVisibilityScene(
        ["blue"],
        observers.Select(body => new MapVisibilityObserver("blue", body,
            body.Manager?.VisibilityStats.VisionRange ?? MapVisibilityArea.Default.DefaultVisionRange)).ToArray(),
        map.Objects.Select(body => new MapVisibilityTarget(body, body.Manager?.VisibilityStats.Stealth ?? 0)).ToArray()));
}
