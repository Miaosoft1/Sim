using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public sealed class MovementState
{
    private readonly MapRuntime? world;
    private readonly MapRuntime? targetWorld;
    private readonly IMapMovement movement;
    private readonly long? controlVersion;
    public Guid Id { get; } = Guid.NewGuid();
    public MapObject Body { get; }
    public MovementMode Mode { get; }
    public MapObject? TrackingTarget { get; }
    public Vector2D Destination { get; private set; }
    public double Speed { get; set; }
    public TimeSpan Interval { get; }
    public TimeSpan LastAt { get; private set; }
    public bool StopAtBoundary { get; }
    public bool IsTorus { get; }
    public double MapWidth { get; }
    public double MapHeight { get; }
    public double ArrivalRadius { get; }
    public bool TargetLost { get; private set; }
    public double TravelledDistance { get; private set; }
    public List<double> Checkpoints { get; } = [];
    public MovementEndReason? EndReason { get; private set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public MovementState(MapObject body, Vector2D destination, double speed, TimeSpan now,
        TimeSpan? interval = null, MovementMode mode = MovementMode.Linear,
        MapObject? trackingTarget = null, bool stopAtBoundary = false,
        bool isTorus = false, double mapWidth = 0, double mapHeight = 0, double arrivalRadius = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enum.IsDefined(mode) || (mode == MovementMode.Homing && trackingTarget == null))
            throw new ArgumentException("Homing movement requires a spatial target.", nameof(mode));
        if (!double.IsFinite(destination.X) || !double.IsFinite(destination.Y))
            throw new ArgumentOutOfRangeException(nameof(destination));
        if (!double.IsFinite(speed) || (mode == MovementMode.Instant ? speed < 0 : speed <= 0))
            throw new ArgumentOutOfRangeException(nameof(speed));
        if (!double.IsFinite(arrivalRadius) || arrivalRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(arrivalRadius));
        Body = body;
        Destination = trackingTarget?.Position ?? destination;
        Speed = speed;
        LastAt = now;
        Interval = MapMovementHandler.ValidateCheckInterval(interval ?? body.MovementInterval);
        Mode = mode;
        TrackingTarget = trackingTarget;
        StopAtBoundary = stopAtBoundary;
        IsTorus = isTorus && mapWidth > 0 && mapHeight > 0;
        MapWidth = mapWidth;
        MapHeight = mapHeight;
        ArrivalRadius = arrivalRadius;
        world = body.MapRuntime;
        targetWorld = trackingTarget?.MapRuntime;
        movement = world?.Movement ?? new OverworldMovement(IsTorus, MapWidth, MapHeight);
        controlVersion = body.Manager?.ControlVersion;
    }

    public double DistanceToDestination => Math.Max(0, movement.Distance(Body.Position, Destination) - ArrivalRadius);

    public TimeSpan NextDelay
    {
        get
        {
            if (Mode == MovementMode.Instant || NextStepDistance <= 1e-9) return TimeSpan.Zero;
            return TimeSpan.FromTicks(Math.Max(1, Math.Min(Interval.Ticks,
                (long)Math.Min(long.MaxValue, Math.Round(NextStepDistance / Speed * TimeSpan.TicksPerSecond)))));
        }
    }

    private double NextStepDistance
    {
        get
        {
            double distance = DistanceToDestination;
            foreach (double checkpoint in Checkpoints)
                if (checkpoint >= TravelledDistance - 1e-8)
                    distance = Math.Min(distance, Math.Max(0, checkpoint - TravelledDistance));
            if (distance > 0 && world != null)
                distance = world.LimitMovementStep(this, distance);
            return distance;
        }
    }

    public void Retarget(Vector2D destination, double speed)
    {
        if (!double.IsFinite(destination.X) || !double.IsFinite(destination.Y) ||
            !double.IsFinite(speed) || speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(destination));
        Destination = destination;
        Speed = speed;
    }

    public MovementEndReason? Advance(TimeSpan now)
    {
        if (EndReason.HasValue) return EndReason;
        if (now < LastAt) throw new InvalidOperationException("Movement cannot run backwards in time.");
        if (!ReferenceEquals(world, Body.MapRuntime) ||
            controlVersion.HasValue && (Body.Manager?.ControlVersion != controlVersion || !Body.CanMove))
            return EndReason = MovementEndReason.MapChanged;
        if (Mode == MovementMode.Homing && !TargetLost)
        {
            if (TrackingTarget!.IsActive && TrackingTarget.IsInteractable && TrackingTarget.IsSelectable &&
                ReferenceEquals(targetWorld, TrackingTarget.MapRuntime) && ReferenceEquals(world, targetWorld))
                Destination = TrackingTarget.Position;
            else
                TargetLost = true;
        }
        double remaining = DistanceToDestination;
        double stepLimit = NextStepDistance;
        double distance = Mode == MovementMode.Instant ? stepLimit : Speed * (now - LastAt).TotalSeconds;
        if (stepLimit <= distance + Speed / TimeSpan.TicksPerSecond / 2 + 1e-9) distance = stepLimit;
        Vector2D proposed = movement.MoveTowards(Body.Position, Destination, Math.Min(distance, stepLimit));
        if (StopAtBoundary)
        {
            var clipped = movement.Clip(Body.Position, proposed);
            proposed = clipped.Position;
            if (clipped.Blocked) EndReason = MovementEndReason.Blocked;
        }
        TravelledDistance += movement.Distance(Body.Position, proposed);
        Checkpoints.RemoveAll(checkpoint => checkpoint <= TravelledDistance + 1e-8);
        Body.Position = proposed;
        LastAt = now;
        if (!EndReason.HasValue && stepLimit >= remaining && remaining <= distance + 1e-9)
            EndReason = TargetLost ? MovementEndReason.Missed : MovementEndReason.Arrived;
        return EndReason;
    }

    public void Stop(MovementEndReason reason, bool replaceEnd = false)
    {
        if (replaceEnd || !EndReason.HasValue) EndReason = reason;
        Cancellation.Cancel();
    }
}
