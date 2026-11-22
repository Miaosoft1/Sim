using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map.EventData;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapMovementHandler : IHandler
    {
        public string Id => nameof(MapMovementHandler);

        [Handler]
        public IEnumerable<IEvent> OnMovement(MapMovementEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var movement = ev.Movement ?? new MovementState(ev.Target, ev.Destination, ev.Speed,
                ev.LastCheckAt, ev.CheckInterval, isTorus: ev.IsTorus,
                mapWidth: ev.MapWidth, mapHeight: ev.MapHeight);
            if (movement.EndReason.HasValue || movement.Cancellation.IsCancellationRequested) yield break;
            TimeSpan now = ev.Context.ContextTimeSpan;
            if (ev.PendingSchedule)
            {
                TimeSpan delay = movement.NextDelay;
                yield return ev with { PendingSchedule = false, TimeSpan = delay, NextCheckAt = now + delay };
                yield break;
            }
            Vector2D from = ev.Target.Position;
            var reason = movement.Advance(now);
            yield return ev.Context.Factory.NewEvent<MapMovementUpdatedEvent>() with
            {
                Movement = movement, From = from, Position = ev.Target.Position, At = now,
                EndReason = reason, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
            };
            if (reason.HasValue)
            {
                if (reason != MovementEndReason.Arrived) yield break;
                if (ev.CollisionTarget is { } collisionTarget)
                    yield return ev.Context.Factory.NewEvent<ProjectileCollisionEvent>() with
                    {
                        Projectile = ev.Target, Target = collisionTarget, Position = ev.Target.Position,
                        Receiver = collisionTarget.EventReceiver, TimeSpan = TimeSpan.Zero
                    };
                yield return ev.Context.Factory.NewEvent<MapMovementCompletedEvent>() with
                {
                    Target = ev.Target,
                    Destination = ev.Target.Position,
                    Receiver = ev.Receiver,
                    TimeSpan = TimeSpan.Zero
                };
                yield break;
            }

            yield return ev.Context.Factory.NewEvent<MapMovementEvent>() with
            {
                Movement = movement,
                CollisionTarget = ev.CollisionTarget,
                Target = ev.Target,
                Destination = ev.Destination,
                Speed = ev.Speed,
                CheckInterval = ev.CheckInterval,
                LastCheckAt = now,
                NextCheckAt = now.Add(movement.NextDelay),
                IsTorus = ev.IsTorus,
                MapWidth = ev.MapWidth,
                MapHeight = ev.MapHeight,
                Receiver = ev.Receiver,
                TimeSpan = movement.NextDelay,
                CancellationTokenSource = ev.CancellationTokenSource,
            };
        }

        [Handler]
        public IEnumerable<IEvent> OnProjectileFlight(ProjectileFlightEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested) yield break;
            var movement = ev.Movement ?? new MovementState(ev.Projectile, ev.Target.Position,
                ev.Speed, ev.LastCheckAt, ev.CheckInterval, MovementMode.Homing, ev.Target,
                arrivalRadius: ev.CollisionRadius + ev.Projectile.CollisionRadius + ev.Target.CollisionRadius);
            yield return ev.Context.Factory.NewEvent<MapMovementEvent>() with
            {
                Movement = movement,
                Target = ev.Projectile,
                Destination = movement.Destination,
                CollisionTarget = ev.Target,
                Speed = ev.Speed,
                CheckInterval = ev.CheckInterval,
                LastCheckAt = ev.LastCheckAt,
                NextCheckAt = ev.Context.ContextTimeSpan,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = movement.Cancellation,
            };
        }

        internal static void ValidateSpeed(double speed)
        {
            if (speed <= 0 || !double.IsFinite(speed))
            {
                throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be positive and finite.");
            }
        }

        internal static TimeSpan ValidateCheckInterval(TimeSpan checkInterval)
        {
            if (checkInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(checkInterval), "Check interval must be positive.");
            }

            return checkInterval;
        }
    }
}
