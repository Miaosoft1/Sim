using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Visibility;

public sealed class MapVisibilityMovementLimiter(
    Func<MapRuntime, MapVisibilitySnapshot> getSnapshot) : IMapMovementStepLimiter
{
    private readonly Func<MapRuntime, MapVisibilitySnapshot> getSnapshot =
        getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));

    public double LimitStep(MovementState movement, double maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (!double.IsFinite(maximumDistance) || maximumDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        if (movement.Body.MapRuntime is not { } map) return maximumDistance;

        double lookahead = movement.Mode == MovementMode.Instant
            ? maximumDistance : Math.Min(maximumDistance, movement.Speed * movement.Interval.TotalSeconds);
        if (lookahead <= 0) return maximumDistance;
        Vector2D origin = movement.Body.Position;
        Vector2D destination = movement.Mode == MovementMode.Homing &&
            movement.TrackingTarget is { IsActive: true, IsInteractable: true, IsSelectable: true } target &&
            ReferenceEquals(target.MapRuntime, map)
                ? target.Position : movement.Destination;
        Vector2D end = map.Movement.MoveTowards(origin, destination, lookahead);
        double actualDistance = map.Movement.Distance(origin, end);
        if (actualDistance <= 0) return maximumDistance;

        MapVisibilitySnapshot snapshot = getSnapshot(map) ??
            throw new InvalidOperationException("The visibility snapshot is required.");
        double ulp = Math.Max(Math.Max(Ulp(origin.X), Ulp(origin.Y)),
            Math.Max(Math.Max(Ulp(end.X), Ulp(end.Y)), Ulp(actualDistance)));
        double resolution = ulp <= actualDistance / 8 ? 8 * ulp : actualDistance;
        double nextStep = actualDistance;
        bool isTarget = snapshot.Targets.Contains(movement.Body);
        foreach (string team in snapshot.Teams)
        {
            if (isTarget) FindBoundary(movement.Body);
            if (snapshot.IsObserver(team, movement.Body))
            {
                foreach (MapObject candidate in snapshot.MovementCandidates(team, movement.Body, origin, end))
                    if (!ReferenceEquals(candidate, movement.Body))
                        FindBoundary(candidate);
            }

            void FindBoundary(MapObject candidate)
            {
                IReadOnlyList<MapVisibilityInterval> intervals = snapshot.TraceVisibility(team, candidate,
                    movement.Body, origin, end);
                for (int index = 0; index < intervals.Count; index++)
                {
                    MapVisibilityInterval interval = intervals[index];
                    if (interval.Start > 0)
                        Consider(interval.Start, interval.End - interval.Start, entering: true);
                    if (interval.End < 1)
                    {
                        double following = index + 1 < intervals.Count ? intervals[index + 1].Start : 1;
                        Consider(interval.End, following - interval.End, entering: false);
                    }
                }
            }

            void Consider(double fraction, double available, bool entering)
            {
                double distance = fraction * actualDistance;
                double offset = entering && available == 0
                    ? 0 : Math.Min(resolution, available * actualDistance / 2);
                double candidate = distance + offset;
                if (candidate <= 0) candidate = Math.BitIncrement(0);
                if (candidate < actualDistance && map.Movement.MoveTowards(origin, end, candidate) == origin)
                    candidate = Math.Min(actualDistance, resolution);
                nextStep = Math.Min(nextStep, candidate);
            }
        }

        return nextStep < actualDistance ? Math.Min(maximumDistance, nextStep) : maximumDistance;
    }

    private static double Ulp(double value)
    {
        double magnitude = Math.Abs(value);
        return magnitude - Math.BitDecrement(magnitude);
    }
}
