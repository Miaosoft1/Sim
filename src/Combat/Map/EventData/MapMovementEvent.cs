using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.EventData
{
    public record class MapMovementEvent : Event
    {
        public required MapObject Target { get; init; }
        public required Vector2D Destination { get; init; }
        public required double Speed { get; init; }
        public MovementState? Movement { get; init; }
        public bool PendingSchedule { get; init; }
        public MapObject? CollisionTarget { get; init; }
        public TimeSpan CheckInterval { get; init; } = MovementTiming.DefaultInterval;
        public TimeSpan LastCheckAt { get; init; }
        public TimeSpan NextCheckAt { get; init; }
        public bool IsTorus { get; init; }
        public double MapWidth { get; init; }
        public double MapHeight { get; init; }

        public static MapMovementEvent Schedule(
            Context context,
            EventReceiver mapReceiver,
            MapObject target,
            Vector2D destination,
            double speed,
            TimeSpan? checkInterval = null,
            bool isTorus = false,
            double mapWidth = 0,
            double mapHeight = 0,
            MovementMode mode = MovementMode.Linear,
            MapObject? trackingTarget = null,
            bool stopAtBoundary = false,
            bool deferFirstStep = false)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(mapReceiver);
            ArgumentNullException.ThrowIfNull(target);
            TimeSpan interval = MapMovementHandler.ValidateCheckInterval(checkInterval ?? target.MovementInterval);
            TimeSpan now = context.ContextTimeSpan;
            var movement = new MovementState(target, destination, speed, now, interval, mode,
                trackingTarget, stopAtBoundary, isTorus, mapWidth, mapHeight);
            target.Movement = movement;
            TimeSpan delay = deferFirstStep ? TimeSpan.Zero : movement.NextDelay;

            return context.Factory.NewEvent<MapMovementEvent>() with
            {
                Movement = movement,
                PendingSchedule = deferFirstStep,
                Target = target,
                Destination = destination,
                Speed = speed,
                CheckInterval = interval,
                LastCheckAt = now,
                NextCheckAt = now.Add(delay),
                IsTorus = isTorus,
                MapWidth = mapWidth,
                MapHeight = mapHeight,
                Receiver = mapReceiver,
                TimeSpan = delay,
                CancellationTokenSource = movement.Cancellation,
            };
        }
    }
}
