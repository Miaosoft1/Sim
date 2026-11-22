using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Spatial;
using System.Runtime.CompilerServices;

namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    /// <summary>
    /// Spatial index of battle units, bridging combat units to world coordinates.
    /// Selectors query units by position/radius/segment through this registry
    /// instead of depending on the map world.
    /// </summary>
    public class UnitSpatialRegistry
    {
        private static readonly ConditionalWeakTable<Context, UnitSpatialRegistry> registries = new();
        private readonly List<Unit> units = new List<Unit>();
        private readonly Dictionary<UnitMapObjectManager, Unit> owners = new();
        private readonly Dictionary<Unit, long> registrationOrder = new();
        private readonly SpatialGrid<Unit> mapless = new(MapSpace.Planar);
        private readonly HashSet<Unit> maplessUnits = new();
        private static readonly double DistanceUnderflowMargin = Math.Sqrt(double.Epsilon);
        private long nextRegistrationOrder;

        public static UnitSpatialRegistry ForContext(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return registries.GetValue(context, _ => new UnitSpatialRegistry());
        }

        public void Register(Unit unit)
        {
            ArgumentNullException.ThrowIfNull(unit);
            if (!units.Contains(unit))
            {
                units.Add(unit);
                registrationOrder.Add(unit, nextRegistrationOrder++);
                owners.Add(unit.MapObjects, unit);
                unit.MapObjects.SpatialChanged += OnSpatialChanged;
                UpdateMapless(unit);
            }
        }

        public bool Unregister(Unit unit)
        {
            ArgumentNullException.ThrowIfNull(unit);
            if (!units.Remove(unit)) return false;
            unit.MapObjects.SpatialChanged -= OnSpatialChanged;
            owners.Remove(unit.MapObjects);
            registrationOrder.Remove(unit);
            if (maplessUnits.Remove(unit)) mapless.Remove(unit);
            return true;
        }

        public IReadOnlyList<Unit> Units => units;
        public static MapObject? ObjectIn(Unit unit, MapRuntime? map) =>
            map == null ? (unit.MapObject?.MapRuntime == null ? unit.MapObject : null) : unit.MapObjects.Find(map);

        /// <summary>
        /// Units (excluding <paramref name="except"/>) whose position is within
        /// <paramref name="radius"/> of <paramref name="center"/>.
        /// </summary>
        public IReadOnlyList<Unit> QueryInRadius(Vector2D center, double radius, Unit? except = null, MapRuntime? map = null)
        {
            if (double.IsNaN(radius) || radius < 0)
                return Array.Empty<Unit>();
            if (map?.TargetingMetric is { } metric && !metric.Contains(center))
                return Array.Empty<Unit>();
            // DistanceTo's subtraction, squares and square root can round an accepted point outside the nominal radius.
            const double roundoffMargin = 8 * 2.2204460492503131e-16;
            double candidateRadius = Math.BitIncrement(radius * (1 + roundoffMargin) + DistanceUnderflowMargin);
            Vector2D minimum = new(center.X - candidateRadius, center.Y - candidateRadius);
            Vector2D maximum = new(center.X + candidateRadius, center.Y + candidateRadius);
            var result = new List<Unit>();
            IReadOnlyList<Unit> candidates = map?.TargetingMetric == null ? Candidates(map, minimum, maximum) : units;
            foreach (Unit unit in candidates)
                if (unit != except && unit.CanParticipate &&
                    ObjectIn(unit, map) is { IsActive: true, IsInteractable: true, IsSelectable: true } body &&
                    (map?.TargetingMetric?.Distance(body.Position, center) ?? body.Position.DistanceTo(center)) <= radius)
                    result.Add(unit);
            return result;
        }

        /// <summary>
        /// Units (excluding <paramref name="except"/>) whose position is within
        /// <paramref name="halfWidth"/> perpendicular distance of the segment
        /// from <paramref name="start"/> towards <paramref name="direction"/>
        /// (need not be normalized) up to <paramref name="length"/>.
        /// </summary>
        public IReadOnlyList<Unit> QueryInLine(Vector2D start, Vector2D direction, double length, double halfWidth, Unit? except = null, MapRuntime? map = null)
        {
            double dirX = direction.X;
            double dirY = direction.Y;
            double dirLength = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (dirLength <= 0)
            {
                return Array.Empty<Unit>();
            }

            double nx = dirX / dirLength;
            double ny = dirY / dirLength;

            if (length < 0 || halfWidth < 0)
                return Array.Empty<Unit>();
            Vector2D minimum, maximum;
            if (double.IsFinite(start.X) && double.IsFinite(start.Y) && double.IsFinite(dirLength) &&
                double.IsFinite(length) && double.IsFinite(halfWidth))
            {
                var end = new Vector2D(start.X + nx * length, start.Y + ny * length);
                minimum = new(Math.Min(start.X, end.X) - halfWidth, Math.Min(start.Y, end.Y) - halfWidth);
                maximum = new(Math.Max(start.X, end.X) + halfWidth, Math.Max(start.Y, end.Y) + halfWidth);
            }
            else
            {
                minimum = new(double.NegativeInfinity, double.NegativeInfinity);
                maximum = new(double.PositiveInfinity, double.PositiveInfinity);
            }

            var result = new List<Unit>();
            foreach (Unit unit in Candidates(map, minimum, maximum))
            {
                if (unit == except || !unit.CanParticipate ||
                    ObjectIn(unit, map) is not { IsActive: true, IsInteractable: true, IsSelectable: true } body)
                    continue;
                double relX = body.Position.X - start.X;
                double relY = body.Position.Y - start.Y;
                double along = relX * nx + relY * ny;
                if (along < 0 || along > length) continue;
                double perp = Math.Abs(relX * ny - relY * nx);
                if (perp <= halfWidth) result.Add(unit);
            }
            return result;
        }

        private void OnSpatialChanged(UnitMapObjectManager manager) => UpdateMapless(owners[manager]);

        private void UpdateMapless(Unit unit)
        {
            if (unit.MapObject is { MapRuntime: null } body)
            {
                if (maplessUnits.Add(unit)) mapless.Add(unit, body.Position);
                else mapless.Update(unit, body.Position);
            }
            else if (maplessUnits.Remove(unit))
            {
                mapless.Remove(unit);
            }
        }

        private IReadOnlyList<Unit> Candidates(MapRuntime? map, Vector2D minimum, Vector2D maximum)
        {
            if (double.IsNaN(minimum.X) || double.IsNaN(minimum.Y) ||
                double.IsNaN(maximum.X) || double.IsNaN(maximum.Y))
            {
                minimum = new(double.NegativeInfinity, double.NegativeInfinity);
                maximum = new(double.PositiveInfinity, double.PositiveInfinity);
            }

            if (units.Count <= 32 ||
                map == null && (long)units.Count <= (long)mapless.Count * 2 && mapless.PrefersOrderedScan(minimum, maximum) ||
                map != null && (long)units.Count <= (long)map.Objects.Count * 2 && map.PrefersSpatialScan(minimum, maximum))
                return units;

            var result = new List<Unit>();
            bool ordered = true;
            long previousOrder = -1;
            if (map == null)
            {
                foreach (Unit unit in mapless.QueryBox(minimum, maximum)) Add(unit);
            }
            else
            {
                foreach (MapObject body in map.QuerySpatialCandidates(minimum, maximum))
                    if (body.Manager != null && owners.TryGetValue(body.Manager, out Unit? unit) &&
                        ReferenceEquals(ObjectIn(unit, map), body)) Add(unit);
            }
            if (!ordered) result.Sort((left, right) => registrationOrder[left].CompareTo(registrationOrder[right]));
            return result;

            void Add(Unit unit)
            {
                long order = registrationOrder[unit];
                ordered &= order > previousOrder;
                previousOrder = order;
                result.Add(unit);
            }
        }
    }
}
