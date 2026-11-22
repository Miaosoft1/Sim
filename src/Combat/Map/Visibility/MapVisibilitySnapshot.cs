using System.Collections.Immutable;
using Miaosoft.Sim.Combat.Map.Spatial;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Visibility;

public sealed class MapVisibilitySnapshot : IMapVisibilityQuery
{
    private readonly MapVisibilityManager manager;
    private readonly ImmutableDictionary<MapObject, MapObject> bodies;
    private readonly ImmutableDictionary<MapObject, double> stealth;
    private readonly ImmutableDictionary<(MapObject Observer, MapObject Target), bool?> overrides;
    private readonly ImmutableDictionary<(string Team, MapObject Target), bool> visible;
    private readonly MapVisibilityObserver[] observers;
    private readonly MapObject[] observerOriginals;
    private readonly MapObject[] observerBodies;
    private readonly MapObject[] targets;
    private readonly string[] teams;
    private readonly Dictionary<(string Team, MapRuntime Map), ObserverIndex> observerIndexes = [];
    private readonly Dictionary<MapRuntime, Lazy<SpatialGrid<MapObject>>> targetIndexes = [];
    private readonly Dictionary<MapObject, int[]> observerIndices;
    private readonly Dictionary<(string Team, MapObject Target), int[]> forcedObservers;
    private readonly Dictionary<string, IReadOnlyList<MapObject>> visibleByTeam = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Team, MapRuntime Map), IReadOnlyList<MapObject>> visibleByMap = [];

    private sealed class ObserverIndex(MapSpace space)
    {
        public SpatialGrid<int> Grid { get; } = new(space);
        public List<int> Indices { get; } = [];
        public double MaximumRange { get; set; }
    }

    public MapVisibilitySnapshot(MapVisibilityArea area, MapVisibilityScene scene)
        : this(area, scene, null, true)
    {
    }

    private MapVisibilitySnapshot(MapVisibilityArea area, MapVisibilityScene scene,
        MapVisibilitySnapshot? previous, bool refreshRules)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(scene.Teams);
        ArgumentNullException.ThrowIfNull(scene.Observers);
        ArgumentNullException.ThrowIfNull(scene.Targets);
        if (scene.Teams.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Visibility teams must have identifiers.", nameof(scene));
        manager = new(area);
        Area = area;
        teams = scene.Teams.Distinct(StringComparer.Ordinal).ToArray();
        targets = scene.Targets.Select(target => target.Body).Distinct().ToArray();
        var bodyBuilder = (previous?.bodies ?? ImmutableDictionary<MapObject, MapObject>.Empty).ToBuilder();
        var changed = new HashSet<MapObject>();
        var currentBodies = targets.Concat(scene.Observers.Select(observer => observer.Body)).ToHashSet();
        foreach (MapObject original in currentBodies)
        {
            ArgumentNullException.ThrowIfNull(original);
            if (!bodyBuilder.TryGetValue(original, out MapObject? frozen) || !SameBody(original, frozen))
            {
                bodyBuilder[original] = Freeze(original);
                changed.Add(original);
            }
        }
        foreach (MapObject removed in bodyBuilder.Keys.Where(body => !currentBodies.Contains(body)).ToArray())
            bodyBuilder.Remove(removed);
        bodies = bodyBuilder.ToImmutable();
        var stealthBuilder = ImmutableDictionary.CreateBuilder<MapObject, double>();
        foreach (MapVisibilityTarget target in scene.Targets)
        {
            if (!double.IsFinite(target.Stealth) || target.Stealth < 0)
                throw new ArgumentException("Target stealth must be finite and nonnegative.", nameof(scene));
            stealthBuilder.Add(target.Body, target.Stealth);
            if (previous == null || previous.stealth.GetValueOrDefault(target.Body, -1) != target.Stealth)
                changed.Add(target.Body);
        }
        stealth = stealthBuilder.ToImmutable();
        observers = scene.Observers.Select(observer =>
        {
            if (string.IsNullOrWhiteSpace(observer.TeamId) ||
                !double.IsFinite(observer.VisionRange) || observer.VisionRange < 0)
                throw new ArgumentException("Observers require a team and finite nonnegative vision.", nameof(scene));
            return observer with { Body = bodies[observer.Body] };
        }).ToArray();
        observerOriginals = scene.Observers.Select(observer => observer.Body).ToArray();
        observerBodies = observerOriginals.Distinct().ToArray();
        bool sameObservers = previous != null && observers.AsSpan().SequenceEqual(previous.observers) &&
            observerOriginals.AsSpan().SequenceEqual(previous.observerOriginals);
        observerIndices = sameObservers ? previous!.observerIndices :
            observerOriginals.Select((body, index) => (body, index))
                .GroupBy(item => item.body).ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray());
        if (sameObservers)
        {
            foreach (var (key, indexed) in previous!.observerIndexes) observerIndexes.Add(key, indexed);
        }
        else
        {
            for (int index = 0; index < observers.Length; index++)
            {
                MapVisibilityObserver observer = observers[index];
                if (!observer.IsAlive || !observer.Body.IsActive || !observer.Body.IsVisible ||
                    observer.Body.MapRuntime is not { } map) continue;
                var key = (observer.TeamId, map);
                if (!observerIndexes.TryGetValue(key, out ObserverIndex? indexed))
                    observerIndexes.Add(key, indexed = new(map.Space));
                indexed.Grid.Add(index, observer.Body.Position);
                indexed.Indices.Add(index);
                indexed.MaximumRange = Math.Max(indexed.MaximumRange, observer.VisionRange);
            }
        }
        foreach (var group in targets.Where(target => bodies[target].MapRuntime != null)
                     .GroupBy(target => bodies[target].MapRuntime!))
        {
            MapRuntime map = group.Key;
            var positions = group.Select(target => (Target: target, Position: bodies[target].Position)).ToArray();
            targetIndexes.Add(map, new Lazy<SpatialGrid<MapObject>>(() =>
            {
                var grid = new SpatialGrid<MapObject>(map.Space);
                foreach (var (target, position) in positions) grid.Add(target, position);
                return grid;
            }, LazyThreadSafetyMode.PublicationOnly));
        }

        var overrideBuilder = (previous?.overrides ??
            ImmutableDictionary<(MapObject, MapObject), bool?>.Empty).ToBuilder();
        int ruleEvaluations = 0;
        var observerSet = observerBodies.ToHashSet();
        var targetSet = targets.ToHashSet();
        var previousObservers = previous?.observerBodies.ToHashSet() ?? [];
        var previousTargets = previous?.targets.ToHashSet() ?? [];
        bool NeedsEvaluation(MapObject observer, MapObject target) =>
            previous == null || refreshRules || changed.Contains(observer) || changed.Contains(target) ||
            !previousObservers.Contains(observer) || !previousTargets.Contains(target);
        foreach (var pair in overrideBuilder.Keys.Where(pair => !observerSet.Contains(pair.Item1) ||
                     !targetSet.Contains(pair.Item2) || NeedsEvaluation(pair.Item1, pair.Item2)).ToArray())
            overrideBuilder.Remove(pair);

        var pairsToEvaluate = new HashSet<(MapObject Observer, MapObject Target)>();
        foreach (var mapGroup in observerBodies.Where(body => body.MapRuntime != null).GroupBy(body => body.MapRuntime!))
        {
            MapRuntime map = mapGroup.Key;
            MapObject[] mapObservers = mapGroup.ToArray();
            IReadOnlyCollection<IMapVisibilityRule> mapRules = map.VisibilityRules;
            var ownerRules = mapObservers.Select(observer =>
                (Observer: observer, Rules: observer.Manager?.VisibilityRules ?? Array.Empty<IMapVisibilityRule>()))
                .ToArray();
            if (mapRules.Count == 0 && ownerRules.All(item => item.Rules.Count == 0)) continue;
            MapObject[] mapTargets = targets.Where(target => target.MapRuntime == map).ToArray();
            var targetsById = mapTargets.GroupBy(target => target.Id)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (IMapVisibilityRule rule in mapRules)
                CollectPairs(rule, mapObservers, mapTargets, targetsById);
            foreach (var (observer, rules) in ownerRules)
            foreach (IMapVisibilityRule rule in rules)
                CollectPairs(rule, [observer], mapTargets, targetsById);
        }
        if (pairsToEvaluate.Count > 0)
        {
            var observerOrder = observerBodies.Select((body, index) => (body, index))
                .ToDictionary(item => item.body, item => item.index);
            var targetOrder = targets.Select((body, index) => (body, index))
                .ToDictionary(item => item.body, item => item.index);
            bool ObserverChanged(MapObject observer) => previous == null || refreshRules ||
                changed.Contains(observer) || !previousObservers.Contains(observer);
            foreach (var (observer, target) in pairsToEvaluate
                         .OrderBy(pair => ObserverChanged(pair.Observer) ? 0 : 1)
                         .ThenBy(pair => ObserverChanged(pair.Observer) ? observerOrder[pair.Observer] : targetOrder[pair.Target])
                         .ThenBy(pair => ObserverChanged(pair.Observer) ? targetOrder[pair.Target] : observerOrder[pair.Observer]))
            {
                bool? result = MapVisibilityManager.GetRuleOverride(observer, target, observer.MapRuntime!.VisibilityRules);
                if (result.HasValue) overrideBuilder[(observer, target)] = result;
                ruleEvaluations++;
            }
        }
        overrides = overrideBuilder.ToImmutable();
        RuleEvaluations = ruleEvaluations;
        var frozenRules = new FrozenRule(overrides, bodies.ToDictionary(pair => pair.Value, pair => pair.Key));
        Rules = [frozenRules];
        var forced = new Dictionary<(string Team, MapObject Target), HashSet<int>>();
        foreach (var (pair, result) in overrides)
        {
            if (result != true || !observerIndices.TryGetValue(pair.Observer, out int[]? indices)) continue;
            foreach (int index in indices)
            {
                MapVisibilityObserver observer = observers[index];
                if (!observer.IsAlive || !observer.Body.IsActive || !observer.Body.IsVisible ||
                    observer.Body.MapRuntime == null || observer.Body.MapRuntime != bodies[pair.Target].MapRuntime)
                    continue;
                var key = (observer.TeamId, pair.Target);
                if (!forced.TryGetValue(key, out HashSet<int>? items))
                    forced.Add(key, items = []);
                items.Add(index);
            }
        }
        forcedObservers = forced.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray());
        var visibleBuilder = (previous?.visible ??
            ImmutableDictionary<(string, MapObject), bool>.Empty).ToBuilder();
        var teamSet = teams.ToHashSet(StringComparer.Ordinal);
        foreach (var key in visibleBuilder.Keys.Where(key => !teamSet.Contains(key.Item1) || !targetSet.Contains(key.Item2)).ToArray())
            visibleBuilder.Remove(key);
        foreach (string team in teams)
        {
            HashSet<MapObject>? affected = previous == null || refreshRules ||
                !previous.teams.Contains(team, StringComparer.Ordinal)
                ? null : AffectedTargets(team, previous, changed);
            IEnumerable<MapObject> affectedTargets = affected == null ? targets : targets.Where(affected.Contains);
            foreach (MapObject target in affectedTargets)
            {
                MapObject frozen = bodies[target];
                bool result = IsVisibleFromCandidates(team, target, frozen, stealth[target]);
                ObjectEvaluations++;
                visibleBuilder[(team, target)] = result;
            }
        }
        visible = visibleBuilder.ToImmutable();

        foreach (string team in teams)
        {
            MapObject[] teamTargets = targets.Where(target => bodies[target].MapRuntime != null &&
                visible.GetValueOrDefault((team, target))).ToArray();
            visibleByTeam.Add(team, Array.AsReadOnly(teamTargets));
            foreach (var group in teamTargets.GroupBy(target => bodies[target].MapRuntime!))
                visibleByMap.Add((team, group.Key), Array.AsReadOnly(group.ToArray()));
        }

        void CollectPairs(IMapVisibilityRule rule, MapObject[] possibleObservers, MapObject[] possibleTargets,
            Dictionary<string, MapObject[]> targetsById)
        {
            if (rule is IScopedMapVisibilityRule scoped)
            {
                var observersById = possibleObservers.GroupBy(observer => observer.Id)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var (viewerId, targetId) in scoped.GetAffectedPairs())
                {
                    if (string.IsNullOrWhiteSpace(viewerId) || string.IsNullOrWhiteSpace(targetId))
                        throw new InvalidOperationException("A scoped visibility rule returned an invalid pair.");
                    if (!observersById.TryGetValue(viewerId, out MapObject[]? matchingObservers) ||
                        !targetsById.TryGetValue(targetId, out MapObject[]? matchingTargets)) continue;
                    foreach (MapObject observer in matchingObservers)
                    foreach (MapObject target in matchingTargets)
                        if (NeedsEvaluation(observer, target)) pairsToEvaluate.Add((observer, target));
                }
                return;
            }

            MapObject[] changedTargets = possibleTargets
                .Where(target => changed.Contains(target) || !previousTargets.Contains(target)).ToArray();
            foreach (MapObject observer in possibleObservers)
            {
                IEnumerable<MapObject> affected = previous == null || refreshRules ||
                    changed.Contains(observer) || !previousObservers.Contains(observer)
                    ? possibleTargets : changedTargets;
                foreach (MapObject target in affected) pairsToEvaluate.Add((observer, target));
            }
        }
    }

    private MapVisibilityArea Area { get; }
    private IMapVisibilityRule[] Rules { get; }
    public int RuleEvaluations { get; }
    public int ObjectEvaluations { get; }
    public IReadOnlyList<string> Teams => Array.AsReadOnly(teams);
    public IReadOnlyList<MapObject> Targets => Array.AsReadOnly(targets);

    public bool IsObserver(string teamId, MapObject body) =>
        bodies.TryGetValue(body, out MapObject? frozen) && observers.Any(observer =>
            observer.TeamId == teamId && observer.Body == frozen &&
            observer.IsAlive && frozen.IsActive && frozen.IsVisible);

    public MapVisibilitySnapshot Update(MapVisibilityScene scene, bool refreshRules = false) =>
        new(Area, scene, this, refreshRules);

    public bool IsVisible(string teamId, MapObject target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(target);
        return visible.GetValueOrDefault((teamId, target));
    }

    public bool IsPositionVisible(string teamId, MapRuntime map, Vector2D position, double stealth = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (!double.IsFinite(stealth) || stealth < 0) throw new ArgumentOutOfRangeException(nameof(stealth));
        if (map == null) return false;
        if (!observerIndexes.TryGetValue((teamId, map), out ObserverIndex? indexed)) return false;
        if (indexed.Indices.Count <= 8)
        {
            foreach (int index in indexed.Indices)
                if (manager.IsPositionVisibleFromObserver(observers[index], map, position, stealth)) return true;
            return false;
        }
        var (minimum, maximum) = Bounds(position, position, indexed.MaximumRange);
        return indexed.Grid.AnyInBox(minimum, maximum,
            index => manager.IsPositionVisibleFromObserver(observers[index], map, position, stealth));
    }

    public IReadOnlyList<MapVisibilityInterval> TraceVisibility(string teamId, MapObject target,
        MapObject movingBody, Vector2D from, Vector2D to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(movingBody);
        if (!double.IsFinite(from.X) || !double.IsFinite(from.Y) || !double.IsFinite(to.X) || !double.IsFinite(to.Y))
            throw new ArgumentOutOfRangeException(nameof(from));
        if (!bodies.TryGetValue(target, out MapObject? frozenTarget))
            throw new ArgumentException("The target is not part of this visibility snapshot.", nameof(target));
        if (!bodies.TryGetValue(movingBody, out MapObject? frozenMover))
            throw new ArgumentException("The moving object is not part of this visibility snapshot.", nameof(movingBody));
        IReadOnlyList<MapVisibilityObserver> candidates = [];
        if (frozenTarget.MapRuntime is { } map)
        {
            if (ReferenceEquals(target, movingBody))
            {
                Vector2D travel = map.Space.Delta(from, to);
                candidates = ObserverCandidates(teamId, map, from, new(from.X + travel.X, from.Y + travel.Y), target);
            }
            else
            {
                candidates = ObserverCandidates(teamId, map, frozenTarget.Position, frozenTarget.Position,
                    target, movingBody);
            }
        }
        return manager.TraceVisibility(teamId, frozenTarget, stealth.GetValueOrDefault(target),
            candidates, frozenMover, from, to, Rules);
    }

    public IReadOnlyList<MapObject> GetVisibleObjects(string teamId, MapRuntime map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(map);
        if (!teams.Contains(teamId, StringComparer.Ordinal))
            throw new ArgumentException("The team is not part of this visibility snapshot.", nameof(teamId));
        return visibleByMap.GetValueOrDefault((teamId, map)) ?? Array.Empty<MapObject>();
    }

    public IReadOnlyList<MapObject> GetVisibleObjects(string teamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        if (!visibleByTeam.TryGetValue(teamId, out IReadOnlyList<MapObject>? result))
            throw new ArgumentException("The team is not part of this visibility snapshot.", nameof(teamId));
        return result;
    }

    internal IReadOnlyList<MapObject> MovementCandidates(string teamId, MapObject movingBody, Vector2D from, Vector2D to)
    {
        if (!bodies.TryGetValue(movingBody, out MapObject? frozen) || frozen.MapRuntime is not { } map ||
            !targetIndexes.TryGetValue(map, out Lazy<SpatialGrid<MapObject>>? indexed) ||
            !observerIndices.TryGetValue(movingBody, out int[]? indices)) return [];
        MapVisibilityObserver[] movingObservers = indices.Select(index => observers[index]).Where(observer =>
            observer.TeamId == teamId && observer.IsAlive && observer.Body.IsActive && observer.Body.IsVisible).ToArray();
        if (movingObservers.Length == 0) return [];
        double range = movingObservers.Max(observer => observer.VisionRange);
        Vector2D travel = map.Space.Delta(from, to);
        var (minimum, maximum) = Bounds(from, new(from.X + travel.X, from.Y + travel.Y), range);
        return indexed.Value.QueryBox(minimum, maximum);
    }

    private HashSet<MapObject> AffectedTargets(string team, MapVisibilitySnapshot previous, HashSet<MapObject> changed)
    {
        var affected = changed.ToHashSet();
        if (observers.AsSpan().SequenceEqual(previous.observers) &&
            observerOriginals.AsSpan().SequenceEqual(previous.observerOriginals)) return affected;
        var currentObservers = ObserversByBody(team);
        var oldObservers = previous.ObserversByBody(team);
        foreach (MapObject body in currentObservers.Keys.Concat(oldObservers.Keys).Distinct())
        {
            MapVisibilityObserver[] current = currentObservers.GetValueOrDefault(body) ?? [];
            MapVisibilityObserver[] old = oldObservers.GetValueOrDefault(body) ?? [];
            if (current.SequenceEqual(old)) continue;
            foreach (MapVisibilityObserver observer in current)
                affected.UnionWith(TargetsNear(observer));
            foreach (MapVisibilityObserver observer in old)
                affected.UnionWith(previous.TargetsNear(observer));
            foreach (var (pair, result) in overrides)
                if (pair.Observer == body && result == true) affected.Add(pair.Target);
            foreach (var (pair, result) in previous.overrides)
                if (pair.Observer == body && result == true) affected.Add(pair.Target);
        }
        return affected;
    }

    private Dictionary<MapObject, MapVisibilityObserver[]> ObserversByBody(string team) =>
        Enumerable.Range(0, observers.Length).Where(index => observers[index].TeamId == team)
            .GroupBy(index => observerOriginals[index])
            .ToDictionary(group => group.Key, group => group.Select(index => observers[index]).ToArray());

    private IReadOnlyList<MapObject> TargetsNear(MapVisibilityObserver observer)
    {
        if (!observer.IsAlive || !observer.Body.IsActive || !observer.Body.IsVisible ||
            observer.Body.MapRuntime is not { } map ||
            !targetIndexes.TryGetValue(map, out Lazy<SpatialGrid<MapObject>>? indexed)) return [];
        var (minimum, maximum) = Bounds(observer.Body.Position, observer.Body.Position, observer.VisionRange);
        return indexed.Value.QueryBox(minimum, maximum);
    }

    private bool IsVisibleFromCandidates(string team, MapObject original, MapObject target, double targetStealth)
    {
        if (!target.IsVisible || target.MapRuntime is not { } map ||
            !observerIndexes.TryGetValue((team, map), out ObserverIndex? indexed)) return false;
        if (forcedObservers.TryGetValue((team, original), out int[]? forced))
            foreach (int index in forced)
                if (manager.IsVisibleFromObserver(observers[index], target, targetStealth, Rules)) return true;
        if (indexed.Indices.Count <= 8)
        {
            foreach (int index in indexed.Indices)
                if (manager.IsVisibleFromObserver(observers[index], target, targetStealth, Rules)) return true;
            return false;
        }
        var (minimum, maximum) = Bounds(target.Position, target.Position, indexed.MaximumRange);
        return indexed.Grid.AnyInBox(minimum, maximum,
            index => manager.IsVisibleFromObserver(observers[index], target, targetStealth, Rules));
    }

    private IReadOnlyList<MapVisibilityObserver> ObserverCandidates(string team, MapRuntime map,
        Vector2D from, Vector2D to, MapObject? target = null, MapObject? includeObserver = null)
    {
        if (!observerIndexes.TryGetValue((team, map), out ObserverIndex? indexed)) return [];
        var (minimum, maximum) = Bounds(from, to, indexed.MaximumRange);
        var indices = indexed.Grid.QueryBox(minimum, maximum).ToHashSet();
        if (target != null && forcedObservers.TryGetValue((team, target), out int[]? forced))
            indices.UnionWith(forced);
        if (includeObserver != null && observerIndices.TryGetValue(includeObserver, out int[]? included))
        {
            foreach (int index in included)
            {
                MapVisibilityObserver observer = observers[index];
                if (observer.TeamId == team && observer.IsAlive && observer.Body.IsActive && observer.Body.IsVisible &&
                    observer.Body.MapRuntime == map)
                    indices.Add(index);
            }
        }
        return indices.Order().Select(index => observers[index]).ToArray();
    }

    private (Vector2D Minimum, Vector2D Maximum) Bounds(Vector2D from, Vector2D to, double range)
    {
        double areaX = Math.Abs(Area.Direction.Y) * (Area.Width / 2) +
            Math.Abs(Area.Direction.X) * (Area.Depth / 2);
        double areaY = Math.Abs(Area.Direction.X) * (Area.Width / 2) +
            Math.Abs(Area.Direction.Y) * (Area.Depth / 2);
        double x = Math.BitIncrement(Math.Max(range, areaX));
        double y = Math.BitIncrement(Math.Max(range, areaY));
        var minimum = new Vector2D(Math.Min(from.X, to.X) - x, Math.Min(from.Y, to.Y) - y);
        var maximum = new Vector2D(Math.Max(from.X, to.X) + x, Math.Max(from.Y, to.Y) + y);
        if (double.IsNaN(minimum.X) || double.IsNaN(minimum.Y) ||
            double.IsNaN(maximum.X) || double.IsNaN(maximum.Y))
            return (new(double.NegativeInfinity, double.NegativeInfinity),
                new(double.PositiveInfinity, double.PositiveInfinity));
        return (minimum, maximum);
    }

    private static bool SameBody(MapObject original, MapObject frozen) =>
        original.Position == frozen.Position && original.MapRuntime == frozen.MapRuntime &&
        original.IsActive == frozen.IsActive && original.IsVisible == frozen.IsVisible &&
        original.IsInteractable == frozen.IsInteractable && original.IsSelectable == frozen.IsSelectable &&
        original.CanMove == frozen.CanMove && original.CanCast == frozen.CanCast;

    private static MapObject Freeze(MapObject original) => original.CopyForVisibility();

    private sealed class FrozenRule(ImmutableDictionary<(MapObject, MapObject), bool?> values,
        Dictionary<MapObject, MapObject> originals) : IMapVisibilityRule
    {
        public bool? GetVisibilityOverride(MapObject viewer, MapObject target) =>
            originals.TryGetValue(viewer, out MapObject? originalViewer) &&
            originals.TryGetValue(target, out MapObject? originalTarget)
                ? values.GetValueOrDefault((originalViewer, originalTarget))
                : null;
    }
}
