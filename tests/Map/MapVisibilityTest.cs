using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;

namespace Miaosoft.Sim.Combat.Map.Tests
{
    [TestClass]
    public class MapVisibilityTest
    {
        [TestMethod]
        public void MapRuntime_InitializesConfiguredDimensionsAndRoomInterior()
        {
            Context context = new();
            MapRuntime map = new();
            EventReceiver receiver = context.Factory.NewEventReceiver();
            MapObject player = new("player", new Vector2D(5, 5), receiver, teamId: "blue");
            MapObject dungeon = new("house", new Vector2D(20, 30), receiver, kind: MapObjectKind.Dungeon);
            MapRoom room = new(dungeon);
            map.AddObject(player);
            map.AddRoom(room);

            Assert.AreEqual(100, map.Width);
            Assert.AreEqual(100, map.Height);
            Assert.AreEqual(new Vector2D(10, 10), room.InteriorSize);
            Assert.AreEqual(player.Size, dungeon.Size);

            Assert.ThrowsExactly<InvalidOperationException>(() => map.InteractWithDungeon(player, room));
            Assert.IsNull(player.CurrentDungeonId);
            Assert.AreEqual(new Vector2D(5, 5), player.Position);
        }

        [TestMethod]
        public void GetVisibleObjects_IncludesNearbyObjectsRegardlessOfTeam()
        {
            Context context = new();
            EventReceiver receiver = context.Factory.NewEventReceiver();
            MapRuntime map = new();
            MapObject viewer = new("viewer", new Vector2D(1, 1), receiver, teamId: "blue");
            MapObject teammate = new("teammate", new Vector2D(2, 2), receiver, teamId: "blue");
            MapObject enemy = new("enemy", new Vector2D(3, 3), receiver, teamId: "red");
            MapObject teammateProjectile = new(
                "teammate-projectile",
                new Vector2D(4, 4),
                receiver,
                kind: MapObjectKind.Projectile,
                ownerId: viewer.Id);
            MapObject enemySummon = new(
                "enemy-summon",
                new Vector2D(5, 5),
                receiver,
                kind: MapObjectKind.Summon,
                ownerId: enemy.Id);
            MapObject dungeon = new("dungeon", new Vector2D(10, 10), receiver, kind: MapObjectKind.Dungeon);

            map.AddObject(viewer);
            map.AddObject(teammate);
            map.AddObject(enemy);
            map.AddObject(teammateProjectile);
            map.AddObject(enemySummon);
            map.AddObject(dungeon);

            var scene = new MapVisibilityScene(["blue"],
                [new("blue", viewer, MapVisibilityArea.Default.DefaultVisionRange),
                 new("blue", teammate, MapVisibilityArea.Default.DefaultVisionRange)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray());
            var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
            string[] visibleIds = map.GetVisibleObjects("blue", snapshot).Select(item => item.Id).ToArray();
            CollectionAssert.AreEquivalent(
                new[] { viewer.Id, teammate.Id, enemy.Id, teammateProjectile.Id, enemySummon.Id, dungeon.Id },
                visibleIds);
        }

        [TestMethod]
        public void GetVisibleObjects_UsesTheExplicitObserverTeamAndLifeState()
        {
            Context context = new();
            EventReceiver receiver = context.Factory.NewEventReceiver();
            MapRuntime map = new(500, 500);
            MapObject scout = new("scout", new Vector2D(200, 200), receiver,
                kind: MapObjectKind.Summon, teamId: "red");
            MapObject deadScout = new("dead", new Vector2D(300, 300), receiver, teamId: "blue");
            MapObject visible = new("visible", new Vector2D(205, 200), receiver);
            MapObject hidden = new("hidden", new Vector2D(305, 300), receiver);
            foreach (MapObject body in new[] { scout, deadScout, visible, hidden }) map.AddObject(body);
            MapVisibilityTarget[] targets = map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray();
            var scene = new MapVisibilityScene(["blue", "red"],
                [new("blue", scout, 5), new("blue", deadScout, 5, IsAlive: false)], targets);

            var snapshot = new MapVisibilitySnapshot(MapVisibilityArea.Default, scene);
            var results = map.GetVisibleObjects("blue", snapshot);
            Assert.IsTrue(results.Contains(visible));
            Assert.IsFalse(results.Contains(hidden));
            var manager = new MapVisibilityManager(MapVisibilityArea.Default);
            CollectionAssert.AreEquivalent(
                targets.Where(target => manager.IsVisible("blue", target.Body, target.Stealth, scene.Observers))
                    .Select(target => target.Body).ToArray(),
                results.ToArray());
            Assert.ThrowsExactly<ArgumentException>(() => map.GetVisibleObjects("missing", snapshot));
        }
    }
}
