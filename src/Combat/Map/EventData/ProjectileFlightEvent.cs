using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.EventData
{
    public record class ProjectileFlightEvent : Event
    {
        public required MapObject Projectile { get; init; }
        public required MapObject Target { get; init; }
        public required double Speed { get; init; }
        public MovementState? Movement { get; init; }
        public double CollisionRadius { get; init; }
        public TimeSpan CheckInterval { get; init; } = MovementTiming.DefaultInterval;
        public TimeSpan LastCheckAt { get; init; }
        public TimeSpan NextCheckAt { get; init; }

        public static ProjectileFlightEvent Schedule(
            Context context,
            EventReceiver mapReceiver,
            MapObject projectile,
            MapObject target,
            double speed,
            double collisionRadius = 0,
            TimeSpan? checkInterval = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(mapReceiver);
            ArgumentNullException.ThrowIfNull(projectile);
            ArgumentNullException.ThrowIfNull(target);
            MapMovementHandler.ValidateSpeed(speed);
            if (collisionRadius < 0 || !double.IsFinite(collisionRadius))
            {
                throw new ArgumentOutOfRangeException(nameof(collisionRadius));
            }

            TimeSpan interval = MapMovementHandler.ValidateCheckInterval(checkInterval ?? projectile.MovementInterval);
            TimeSpan now = context.ContextTimeSpan;
            var movement = new MovementState(projectile, target.Position, speed, now, interval,
                MovementMode.Homing, target,
                arrivalRadius: collisionRadius + projectile.CollisionRadius + target.CollisionRadius);
            projectile.Movement = movement;

            return context.Factory.NewEvent<ProjectileFlightEvent>() with
            {
                Projectile = projectile,
                Movement = movement,
                Target = target,
                Speed = speed,
                CollisionRadius = collisionRadius,
                CheckInterval = interval,
                LastCheckAt = now,
                NextCheckAt = now.Add(movement.NextDelay),
                Receiver = mapReceiver,
                TimeSpan = movement.NextDelay,
                CancellationTokenSource = movement.Cancellation,
            };
        }
    }
}
