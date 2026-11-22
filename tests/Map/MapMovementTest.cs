using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.EventData;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Tests
{
    [TestClass]
    public class MapMovementTest
    {
        [TestMethod]
        public void MovementEvent_MovesObjectToDestinationAtConfiguredSpeed()
        {
            Context context = new Context();
            EventReceiver mapReceiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(mapReceiver);
            mapReceiver.AddHandler(new MapMovementHandler());

            MapObject unit = new MapObject("unit", new Vector2D(0, 0), context.Factory.NewEventReceiver());
            MapMovementEvent movement = MapMovementEvent.Schedule(
                context,
                mapReceiver,
                unit,
                new Vector2D(10, 0),
                speed: 2,
                checkInterval: TimeSpan.FromSeconds(1));

            Assert.AreEqual(TimeSpan.FromSeconds(1), movement.NextCheckAt);
            context.Dispatch(movement).ToList();
            context.RunUntilQueueEmpty();

            Assert.AreEqual(new Vector2D(10, 0), unit.Position);
            Assert.AreEqual(TimeSpan.FromSeconds(5), context.ContextTimeSpan);
        }

        [TestMethod]
        public void ProjectileFlightEvent_HitsWhenTargetIsWithinNextStepDistance()
        {
            Context context = new Context();
            EventReceiver mapReceiver = context.Factory.NewEventReceiver();
            EventReceiver targetReceiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(mapReceiver);
            context.AddEventReceiver(targetReceiver);
            mapReceiver.AddHandler(new MapMovementHandler());

            MapObject projectile = new MapObject("projectile", new Vector2D(0, 0), mapReceiver);
            MapObject target = new MapObject("target", new Vector2D(10, 0), targetReceiver);
            ProjectileFlightEvent flight = ProjectileFlightEvent.Schedule(
                context,
                mapReceiver,
                projectile,
                target,
                speed: 6,
                checkInterval: TimeSpan.FromSeconds(1));

            Assert.AreEqual(TimeSpan.FromSeconds(1), flight.NextCheckAt);
            context.Dispatch(flight).ToList();
            context.RunUntilQueueEmpty();

            ProjectileCollisionEvent collision = targetReceiver.History
                .OfType<ProjectileCollisionEvent>()
                .Single();
            Assert.AreSame(projectile, collision.Projectile);
            Assert.AreSame(target, collision.Target);
            Assert.AreEqual(new Vector2D(10, 0), projectile.Position);
            Assert.AreEqual(TimeSpan.FromTicks(16666667), collision.Execute);
            Assert.HasCount(2, mapReceiver.History.OfType<MapMovementUpdatedEvent>());
        }

        [TestMethod]
        public void ProjectileFlightEvent_UsesObjectCollisionRadius()
        {
            Context context = new Context();
            EventReceiver mapReceiver = context.Factory.NewEventReceiver();
            EventReceiver targetReceiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(mapReceiver);
            context.AddEventReceiver(targetReceiver);
            mapReceiver.AddHandler(new MapMovementHandler());

            MapObject projectile = new MapObject("projectile", new Vector2D(0, 0), mapReceiver, collisionRadius: 1);
            MapObject target = new MapObject("target", new Vector2D(10, 0), targetReceiver, collisionRadius: 2);
            ProjectileFlightEvent flight = ProjectileFlightEvent.Schedule(
                context,
                mapReceiver,
                projectile,
                target,
                speed: 5,
                checkInterval: TimeSpan.FromSeconds(1));

            context.Dispatch(flight).ToList();
            context.RunUntilQueueEmpty();

            ProjectileCollisionEvent collision = targetReceiver.History
                .OfType<ProjectileCollisionEvent>()
                .Single();
            Assert.AreEqual(new Vector2D(7, 0), projectile.Position);
            Assert.AreEqual(projectile.Position, collision.Position);
        }
    }
}
