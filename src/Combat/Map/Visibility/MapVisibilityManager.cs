using System.Numerics;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Visibility;

public readonly record struct MapVisibilityObserver(
    string TeamId, MapObject Body, double VisionRange, bool IsAlive = true);

public sealed class MapVisibilityManager(MapVisibilityArea area)
{
    private readonly MapVisibilityArea area = area ?? throw new ArgumentNullException(nameof(area));

    public bool IsVisible(string teamId, MapObject target, double stealth,
        IEnumerable<MapVisibilityObserver> observers, IEnumerable<IMapVisibilityRule>? rules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observers);
        if (!double.IsFinite(stealth) || stealth < 0) throw new ArgumentOutOfRangeException(nameof(stealth));
        if (!target.IsVisible || target.MapRuntime == null) return false;

        foreach (MapVisibilityObserver observer in observers)
        {
            if (string.Equals(observer.TeamId, teamId, StringComparison.Ordinal) &&
                IsVisibleFromObserver(observer, target, stealth, rules)) return true;
        }

        return false;
    }

    internal bool IsVisibleFromObserver(MapVisibilityObserver observer, MapObject target, double stealth,
        IEnumerable<IMapVisibilityRule>? rules)
    {
        if (!target.IsVisible || target.MapRuntime is not { } map || !observer.IsAlive ||
            !observer.Body.IsActive || !observer.Body.IsVisible || observer.Body.MapRuntime != map)
            return false;
        if (!double.IsFinite(observer.VisionRange) || observer.VisionRange < 0)
            throw new ArgumentOutOfRangeException("observers", "Vision range must be finite and nonnegative.");
        if (area.Contains(map, observer.Body.Position, target.Position)) return true;
        bool visible = WithinDistance(map, observer.Body.Position, target.Position,
            Math.Max(0, observer.VisionRange - stealth));
        return GetRuleOverride(observer.Body, target, rules) ?? visible;
    }

    internal static bool? GetRuleOverride(MapObject observer, MapObject target,
        IEnumerable<IMapVisibilityRule>? rules)
    {
        bool? result = null;
        if (rules != null)
            foreach (IMapVisibilityRule rule in rules)
                result = rule.GetVisibilityOverride(observer, target) ?? result;
        if (observer.Manager != null)
            foreach (IMapVisibilityRule rule in observer.Manager.VisibilityRules)
                result = rule.GetVisibilityOverride(observer, target) ?? result;
        return result;
    }

    public bool IsPositionVisible(string teamId, MapRuntime? map, Vector2D position, double stealth,
        IEnumerable<MapVisibilityObserver> observers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(observers);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (!double.IsFinite(stealth) || stealth < 0) throw new ArgumentOutOfRangeException(nameof(stealth));
        if (map == null) return false;
        foreach (MapVisibilityObserver observer in observers)
        {
            if (string.Equals(observer.TeamId, teamId, StringComparison.Ordinal) &&
                IsPositionVisibleFromObserver(observer, map, position, stealth)) return true;
        }
        return false;
    }

    internal bool IsPositionVisibleFromObserver(MapVisibilityObserver observer, MapRuntime map,
        Vector2D position, double stealth)
    {
        if (!observer.IsAlive || !observer.Body.IsActive || !observer.Body.IsVisible ||
            observer.Body.MapRuntime != map) return false;
        if (!double.IsFinite(observer.VisionRange) || observer.VisionRange < 0)
            throw new ArgumentOutOfRangeException("observers", "Vision range must be finite and nonnegative.");
        return area.Contains(map, observer.Body.Position, position) ||
            WithinDistance(map, observer.Body.Position, position, Math.Max(0, observer.VisionRange - stealth));
    }

    private static bool WithinDistance(MapRuntime map, Vector2D origin, Vector2D target, double radius)
    {
        Vector2D delta = map.Space.Delta(origin, target);
        return WithinRadius(delta.X, delta.Y, radius);
    }

    private static bool WithinRadius(double x, double y, double radius)
    {
        double major = Math.Max(Math.Abs(x), Math.Abs(y));
        double minor = Math.Min(Math.Abs(x), Math.Abs(y));
        if (major > radius) return false;
        if (minor == 0) return true;
        if (major == radius) return false;
        double minorRatio = minor / radius;
        double squaredMinor = minorRatio * minorRatio;
        double remainingSquared = (radius - major) / radius * (1 + major / radius);
        const double roundingBound = 16 * 2.2204460492503131e-16;
        if (Math.Abs(squaredMinor - remainingSquared) > roundingBound)
            return squaredMinor < remainingSquared;
        return ExactWithinRadius(major, minor, radius);
    }

    private static bool ExactWithinRadius(double x, double y, double radius)
    {
        static (ulong Significand, int Exponent) Parts(double value)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
            int exponent = (int)((bits >> 52) & 0x7ff);
            ulong significand = bits & 0x000f_ffff_ffff_ffff;
            return exponent == 0 ? (significand, -1074) :
                (significand | 0x0010_0000_0000_0000, exponent - 1075);
        }

        var (xSignificand, xExponent) = Parts(x);
        var (ySignificand, yExponent) = Parts(y);
        var (radiusSignificand, radiusExponent) = Parts(radius);
        int exponent = 2 * Math.Min(xExponent, Math.Min(yExponent, radiusExponent));
        BigInteger xSquared = (BigInteger)xSignificand * xSignificand << (2 * xExponent - exponent);
        BigInteger ySquared = (BigInteger)ySignificand * ySignificand << (2 * yExponent - exponent);
        BigInteger radiusSquared =
            (BigInteger)radiusSignificand * radiusSignificand << (2 * radiusExponent - exponent);
        return xSquared + ySquared <= radiusSquared;
    }

    public IReadOnlyList<MapVisibilityInterval> TraceVisibility(string teamId, MapObject target, double stealth,
        IEnumerable<MapVisibilityObserver> observers, MapObject movingBody, Vector2D from, Vector2D to,
        IEnumerable<IMapVisibilityRule>? rules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(movingBody);
        if (!double.IsFinite(stealth) || stealth < 0) throw new ArgumentOutOfRangeException(nameof(stealth));
        if (!double.IsFinite(from.X) || !double.IsFinite(from.Y) || !double.IsFinite(to.X) || !double.IsFinite(to.Y))
            throw new ArgumentOutOfRangeException(nameof(from));
        if (!target.IsVisible || target.MapRuntime is not { } map) return [];

        var candidates = observers.ToArray();
        if (!ReferenceEquals(movingBody.MapRuntime, map))
            return IsVisible(teamId, target, stealth, candidates, rules) ? [new(0, 1)] : [];
        Vector2D travel = map.Space.Delta(from, to);
        var intervals = new List<MapVisibilityInterval>();
        foreach (MapVisibilityObserver observer in candidates)
        {
            if (!string.Equals(observer.TeamId, teamId, StringComparison.Ordinal) || !observer.IsAlive ||
                !observer.Body.IsActive || !observer.Body.IsVisible || !ReferenceEquals(observer.Body.MapRuntime, map))
                continue;
            if (!double.IsFinite(observer.VisionRange) || observer.VisionRange < 0)
                throw new ArgumentOutOfRangeException(nameof(observers), "Vision range must be finite and nonnegative.");
            bool? visibilityOverride = GetRuleOverride(observer.Body, target, rules);
            if (visibilityOverride == true) return [new(0, 1)];

            Vector2D targetStart = ReferenceEquals(target, movingBody) ? from : target.Position;
            Vector2D observerStart = ReferenceEquals(observer.Body, movingBody) ? from : observer.Body.Position;
            double dx = (ReferenceEquals(target, movingBody) ? travel.X : 0) -
                (ReferenceEquals(observer.Body, movingBody) ? travel.X : 0);
            double dy = (ReferenceEquals(target, movingBody) ? travel.Y : 0) -
                (ReferenceEquals(observer.Body, movingBody) ? travel.Y : 0);
            Vector2D initial = map.Space.Delta(observerStart, targetStart);
            double x = initial.X;
            double y = initial.Y;
            var cuts = new List<double> { 0, 1 };
            if (map.Space.IsWrapped)
            {
                AddWrapCuts(cuts, x, dx, map.Space.Width);
                AddWrapCuts(cuts, y, dy, map.Space.Height);
            }
            cuts.Sort();
            for (int index = 1; index < cuts.Count; index++)
            {
                double start = cuts[index - 1], end = cuts[index];
                if (start == end) continue;
                double shiftedX = x, shiftedY = y;
                if (map.Space.IsWrapped)
                {
                    double midpoint = (start + end) / 2;
                    shiftedX -= Math.Round((x + dx * midpoint) / map.Space.Width) * map.Space.Width;
                    shiftedY -= Math.Round((y + dy * midpoint) / map.Space.Height) * map.Space.Height;
                }
                if (visibilityOverride != false)
                    AddCircle(intervals, shiftedX, shiftedY, dx, dy,
                        Math.Max(0, observer.VisionRange - stealth), start, end);
                AddArea(intervals, shiftedX, shiftedY, dx, dy, start, end);
            }
        }

        if (intervals.Count == 0) return [];
        intervals.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<MapVisibilityInterval>();
        MapVisibilityInterval current = intervals[0];
        foreach (MapVisibilityInterval next in intervals.Skip(1))
        {
            if (next.Start <= current.End)
                current = new(current.Start, Math.Max(current.End, next.End));
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }

    private static void AddWrapCuts(List<double> cuts, double start, double delta, double size)
    {
        if (delta == 0) return;
        foreach (double boundary in new[] { -size / 2, size / 2 })
        {
            double at = (boundary - start) / delta;
            if (at > 0 && at < 1) cuts.Add(at);
        }
    }

    private static void AddCircle(List<MapVisibilityInterval> intervals, double x, double y, double dx, double dy,
        double radius, double start, double end)
    {
        double scale = Math.Max(radius, Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)),
            Math.Max(Math.Abs(dx), Math.Abs(dy))));
        if (scale == 0)
        {
            intervals.Add(new(start, end));
            return;
        }
        x /= scale;
        y /= scale;
        dx /= scale;
        dy /= scale;
        radius /= scale;
        double a = dx * dx + dy * dy;
        if (a == 0)
        {
            if (WithinRadius(x, y, radius)) intervals.Add(new(start, end));
            return;
        }
        double perpendicular = Math.Abs(Math.FusedMultiplyAdd(x, dy, -y * dx)) / Math.Sqrt(a);
        if (perpendicular > radius) return;
        double center = -(x * dx + y * dy) / a;
        double halfChord = Math.Sqrt((radius - perpendicular) * (radius + perpendicular) / a);
        double first = center - halfChord;
        double last = center + halfChord;
        first = Math.Max(first, start);
        last = Math.Min(last, end);
        if (first <= last) intervals.Add(new(first, last));
    }

    private void AddArea(List<MapVisibilityInterval> intervals, double x, double y, double dx, double dy,
        double start, double end)
    {
        Vector2D direction = area.Direction;
        double right = x * direction.Y - y * direction.X;
        double rightDelta = dx * direction.Y - dy * direction.X;
        double depth = x * direction.X + y * direction.Y;
        double depthDelta = dx * direction.X + dy * direction.Y;
        if (ClipAxis(right, rightDelta, area.Width / 2, ref start, ref end) &&
            ClipAxis(depth, depthDelta, area.Depth / 2, ref start, ref end))
            intervals.Add(new(start, end));
    }

    private static bool ClipAxis(double origin, double delta, double halfSize, ref double start, ref double end)
    {
        if (delta == 0) return Math.Abs(origin) <= halfSize;
        double first = (-halfSize - origin) / delta;
        double last = (halfSize - origin) / delta;
        if (first > last) (first, last) = (last, first);
        start = Math.Max(start, first);
        end = Math.Min(end, last);
        return start <= end;
    }
}
