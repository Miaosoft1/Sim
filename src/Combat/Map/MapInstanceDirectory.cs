using System.Collections.ObjectModel;

namespace Miaosoft.Sim.Combat.Map;

public sealed class MapInstanceDirectory
{
    private readonly Dictionary<Guid, MapRuntime> maps = [];
    private readonly ReadOnlyDictionary<Guid, MapRuntime> view;
    private readonly HashSet<IMapMovementStepLimiter> movementStepLimiters = new(ReferenceEqualityComparer.Instance);

    public MapInstanceDirectory() => view = new(maps);

    public IReadOnlyDictionary<Guid, MapRuntime> Maps => view;
    public event Action<MapObject, bool>? VisibilityChanged;

    public void Register(MapRuntime map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!ReferenceEquals(map.Root, map) || map.EntryMap != null || map.IsTemporary)
            throw new InvalidOperationException("Only a root map can be registered as a map tree.");
        MapRuntime[] tree = [map, .. map.SubMaps];
        ValidateRegistration(tree);
        RegisterAttached(tree);
    }

    public bool Unregister(MapRuntime map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!ReferenceEquals(map.Directory, this)) return false;
        if (!ReferenceEquals(map.Root, map))
            throw new InvalidOperationException("Only a root map tree can be unregistered.");
        MapRuntime[] tree = [map, .. map.SubMaps];
        if (tree.Any(member => member.Objects.Count != 0))
            throw new InvalidOperationException("A map tree containing objects cannot be unregistered.");
        foreach (MapRuntime member in tree) UnregisterAttached(member);
        return true;
    }

    internal void ValidateRegistration(IEnumerable<MapRuntime> members)
    {
        foreach (MapRuntime member in members)
            if (member.Directory != null || member.IsClosing || member.HasMovementStepLimiters ||
                maps.ContainsKey(member.InstanceId))
                throw new InvalidOperationException("A map is already registered or cannot join this directory.");
    }

    internal void RegisterAttached(IEnumerable<MapRuntime> members)
    {
        foreach (MapRuntime member in members)
        {
            maps.Add(member.InstanceId, member);
            member.AssignDirectory(this);
        }
    }

    internal void UnregisterAttached(MapRuntime map)
    {
        if (!ReferenceEquals(map.Directory, this) || !maps.Remove(map.InstanceId))
            throw new InvalidOperationException("The map directory is inconsistent.");
        map.AssignDirectory(null);
    }

    internal void NotifyVisibilityChanged(MapObject body, bool refreshRules) =>
        VisibilityChanged?.Invoke(body, refreshRules);

    public IDisposable RegisterMovementStepLimiter(IMapMovementStepLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        if (!movementStepLimiters.Add(limiter))
            throw new InvalidOperationException("The movement step limiter is already registered.");
        return new MovementStepLimiterLease(this, limiter);
    }

    internal double LimitMovementStep(MovementState movement, double maximumDistance)
    {
        double distance = maximumDistance;
        foreach (IMapMovementStepLimiter limiter in movementStepLimiters)
        {
            double limited = limiter.LimitStep(movement, distance);
            if (!double.IsFinite(limited) || limited <= 0 || limited > distance)
                throw new InvalidOperationException("A movement step limiter returned an invalid distance.");
            distance = limited;
        }
        return distance;
    }

    private sealed class MovementStepLimiterLease(MapInstanceDirectory directory, IMapMovementStepLimiter limiter) : IDisposable
    {
        private MapInstanceDirectory? owner = directory;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.movementStepLimiters.Remove(limiter);
    }
}
