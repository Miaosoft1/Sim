using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;

namespace Miaosoft.Sim.Combat.Map;

public sealed class UnitMapObjectManager
{
    private readonly Dictionary<Guid, MapObject> objects = [];
    private readonly LinkedList<MapObject> controlPath = [];
    private readonly HashSet<MapObject> inactiveParallelObjects = [];
    private readonly Dictionary<MapObject, (bool Visible, bool Interactable, bool Selectable, bool Move, bool Cast)> savedStates = [];
    private readonly Dictionary<MapObject, (bool Active, bool Move, bool Cast)> deactivatedStates = [];
    private readonly MapVisibilityRuleRegistry visibilityRules = new();
    private readonly ReadOnlyDictionary<Guid, MapObject> view;
    private bool isDeactivated;

    public UnitMapObjectManager(string unitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        UnitId = unitId;
        view = new(objects);
        VisibilityStats.Changed += NotifyVisibilityChanged;
        visibilityRules.Changed += NotifyVisibilityChanged;
    }

    public string UnitId { get; }
    public UnitVisibilityStats VisibilityStats { get; } = new();
    internal IReadOnlyCollection<IMapVisibilityRule> VisibilityRules => visibilityRules.Rules;
    public IReadOnlyDictionary<Guid, MapObject> Objects => view;
    public MapObject? ControlledObject => controlPath.Last?.Value;
    public Guid? ControlledMapInstanceId => ControlledObject?.MapRuntime?.InstanceId;
    public long ControlVersion { get; private set; }
    public bool CanReturn => controlPath.Count > 1;
    public MapRuntime? ReturnMap => controlPath.Last?.Previous?.Value.MapRuntime;
    public event Action<UnitMapObjectManager>? SpatialChanged;

    internal bool IsControlPathObject(MapObject body) => controlPath.Contains(body);

    internal void NotifySpatialChanged(MapObject body)
    {
        if (ReferenceEquals(body, ControlledObject)) SpatialChanged?.Invoke(this);
    }

    public IDisposable RegisterVisibilityRule(IMapVisibilityRule rule) => visibilityRules.Register(rule);

    private void NotifyVisibilityChanged() =>
        (ControlledObject ?? objects.Values.FirstOrDefault())?.NotifyVisibilityChanged(refreshRules: true);

    public void Initialize(MapObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (isDeactivated)
            throw new InvalidOperationException("A deactivated unit cannot initialize a map object.");
        if (controlPath.Count != 0 || body.Manager != null || body.Id != UnitId)
            throw new InvalidOperationException("The initial map object must belong to this unit and be unassigned.");
        body.Manager = this;
        objects.Add(body.MapRuntime?.InstanceId ?? Guid.Empty, body);
        controlPath.AddLast(body);
        ControlVersion++;
        SpatialChanged?.Invoke(this);
    }

    public MapObject? Find(MapRuntime map) => objects.GetValueOrDefault(map.InstanceId);

    public void Register(MapRuntime map, MapObject body)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(body);
        if (isDeactivated)
            throw new InvalidOperationException("A deactivated unit cannot register another map object.");
        if (body.Id != UnitId || body.Manager != null ||
            ControlledObject is { } source && body.EventReceiver != source.EventReceiver)
            throw new InvalidOperationException("The map object must belong to this unit and be unassigned.");
        if (objects.ContainsKey(map.InstanceId) || map.FindObject(UnitId) != null ||
            body.MapRuntime != null || map.IsClosing || !map.Contains(body.Position))
            throw new InvalidOperationException("The map object cannot be registered in the target map.");
        body.Manager = this;
        body.CanMove = body.CanCast = false;
        map.AddObject(body);
    }

    public void SetState(MapRuntime map, bool? isVisible = null, bool? isInteractable = null,
        bool? isSelectable = null, bool? canMove = null, bool? canCast = null)
    {
        var body = Find(map) ?? throw new InvalidOperationException("The unit has no object in this map.");
        if (isVisible.HasValue) body.IsVisible = isVisible.Value;
        if (isInteractable.HasValue) body.IsInteractable = isInteractable.Value;
        if (isSelectable.HasValue) body.IsSelectable = isSelectable.Value;
        if (canMove.HasValue)
        {
            if (isDeactivated)
            {
                var state = deactivatedStates[body];
                deactivatedStates[body] = (state.Active, canMove.Value, state.Cast);
            }
            else body.CanMove = canMove.Value;
            if (!canMove.Value)
            {
                body.Movement?.Stop(MovementEndReason.Stopped);
                body.MapRuntime?.UnitGrid?.Cancel(body);
            }
        }
        if (canCast.HasValue)
        {
            if (isDeactivated)
            {
                var state = deactivatedStates[body];
                deactivatedStates[body] = (state.Active, state.Move, canCast.Value);
            }
            else body.CanCast = canCast.Value;
        }
    }

    internal void ValidateRegistration(MapRuntime map, MapObject body)
    {
        if (body.Manager != this || objects.TryGetValue(map.InstanceId, out var registered) && registered != body)
            throw new InvalidOperationException("The unit already has a different object in this map.");
    }

    internal void Registered(MapRuntime map, MapObject body)
    {
        if (objects.GetValueOrDefault(Guid.Empty) == body) objects.Remove(Guid.Empty);
        objects[map.InstanceId] = body;
    }

    internal void Removed(MapRuntime map, MapObject body)
    {
        objects.Remove(map.InstanceId);
        inactiveParallelObjects.Remove(body);
        deactivatedStates.Remove(body);
        var node = controlPath.Find(body);
        if (node == null) return;
        bool controlled = node == controlPath.Last;
        controlPath.Remove(node);
        savedStates.Remove(body);
        if (controlled)
        {
            RestoreCurrent();
            ControlVersion++;
            SpatialChanged?.Invoke(this);
        }
    }

    public MapObject Enter(MapRuntime map, Vector2D position, TimeSpan now,
        bool keepSourceVisible, string? dungeonId = null, bool appearanceOverlay = false,
        bool preserveDungeonContext = true)
    {
        ArgumentNullException.ThrowIfNull(map);
        MapObject source = ControlledObject ?? throw new InvalidOperationException("There is no controlled map object.");
        if (!source.IsActive || map.IsClosing || !map.Contains(position))
            throw new InvalidOperationException("The target map or position is unavailable.");
        if (source.Movement is { EndReason: null } movement && now < movement.LastAt)
            throw new ArgumentOutOfRangeException(nameof(now));
        var target = Find(map);
        if (target != null && controlPath.Contains(target))
            throw new InvalidOperationException("Use Return to restore an earlier controlled object.");
        if (target == null)
        {
            target = new MapObject(UnitId, position, source.EventReceiver, source.CollisionRadius,
                source.Kind, source.TeamId, source.OwnerId, source.Size) { Manager = this };
            map.AddObject(target);
        }
        source.Movement?.Advance(now);
        source.Movement?.Stop(MovementEndReason.MapChanged);
        savedStates.Add(source, (source.IsVisible, source.IsInteractable, source.IsSelectable, source.CanMove, source.CanCast));
        source.IsVisible &= keepSourceVisible;
        source.IsSelectable &= keepSourceVisible;
        source.CanMove = source.CanCast = false;
        target.Position = position;
        target.CurrentDungeonId = dungeonId ?? (preserveDungeonContext ? source.CurrentDungeonId : null);
        target.PositionBeforeEnteringDungeon = dungeonId != null ? source.Position :
            preserveDungeonContext ? source.PositionBeforeEnteringDungeon : null;
        target.HasAppearanceOverlay = appearanceOverlay;
        target.IsActive = target.IsVisible = target.IsInteractable = target.IsSelectable = target.CanMove = target.CanCast = true;
        target.MovementInterval = source.MovementInterval;
        controlPath.AddLast(target);
        ControlVersion++;
        SpatialChanged?.Invoke(this);
        target.NotifyVisibilityChanged();
        return target;
    }

    public bool CanSwitchParallelPlane(MapRuntime map, Vector2D position, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(map);
        MapObject? source = ControlledObject;
        MapRuntime? sourceMap = source?.MapRuntime;
        if (source == null || sourceMap == null || controlPath.Count != 1 || !source.IsActive ||
            map.IsClosing || ReferenceEquals(sourceMap, map) || sourceMap.Directory == null ||
            !ReferenceEquals(sourceMap.Directory, map.Directory) ||
            sourceMap.EntryMap != null || map.EntryMap != null || sourceMap.IsTemporary || map.IsTemporary ||
            !map.Contains(position) || source.Movement is { EndReason: null } movement && now < movement.LastAt)
            return false;
        MapObject? target = Find(map);
        return target == null ? map.FindObject(UnitId) == null :
            ReferenceEquals(target.Manager, this) && ReferenceEquals(target.MapRuntime, map) &&
            ReferenceEquals(map.FindObject(UnitId), target);
    }

    public MapObject SwitchParallelPlane(MapRuntime map, Vector2D position, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (ControlledObject?.MapRuntime?.UnitGrid != null || map.UnitGrid != null)
            throw new InvalidOperationException("Grid units must transfer through their board occupancy protocol.");
        return SwitchParallelPlaneCore(map, position, now, gridTransfer: false);
    }

    internal MapObject SwitchGridParallelPlane(MapRuntime map, Vector2D position, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (ControlledObject?.MapRuntime?.UnitGrid == null || map.UnitGrid == null)
            throw new InvalidOperationException("A grid transfer requires a grid on both planes.");
        return SwitchParallelPlaneCore(map, position, now, gridTransfer: true);
    }

    private MapObject SwitchParallelPlaneCore(MapRuntime map, Vector2D position, TimeSpan now, bool gridTransfer)
    {
        if (!CanSwitchParallelPlane(map, position, now))
            throw new InvalidOperationException("The target is not an available parallel plane.");
        MapObject source = ControlledObject!;
        MapObject? target = Find(map);
        if (target == null)
        {
            target = new MapObject(UnitId, position, source.EventReceiver, source.CollisionRadius,
                source.Kind, source.TeamId, source.OwnerId, source.Size) { Manager = this, IsActive = false };
            target.IsVisible = target.IsInteractable = target.IsSelectable = target.CanMove = target.CanCast = false;
        }

        using (source.SuspendVisibilityNotifications())
        using (target.SuspendVisibilityNotifications())
        {
            if (target.MapRuntime == null) map.AddObject(target);
            if (!gridTransfer)
            {
                source.Movement?.Advance(now);
                source.Movement?.Stop(MovementEndReason.MapChanged);
            }
            var state = (source.IsVisible, source.IsInteractable, source.IsSelectable, source.CanMove, source.CanCast);
            source.IsActive = source.IsVisible = source.IsInteractable = source.IsSelectable = source.CanMove = source.CanCast = false;
            target.Position = position;
            target.MovementInterval = source.MovementInterval;
            target.IsActive = true;
            (target.IsVisible, target.IsInteractable, target.IsSelectable, target.CanMove, target.CanCast) = state;
            inactiveParallelObjects.Add(source);
            inactiveParallelObjects.Remove(target);
            controlPath.Clear();
            savedStates.Clear();
            controlPath.AddLast(target);
            ControlVersion++;
        }

        SpatialChanged?.Invoke(this);
        source.NotifyVisibilityChanged();
        target.NotifyVisibilityChanged();
        return target;
    }

    public MapObject Return(TimeSpan now)
    {
        if (!CanReturn) throw new InvalidOperationException("There is no earlier map object to restore.");
        var leaving = ControlledObject!;
        leaving.Movement?.Advance(now);
        leaving.Movement?.Stop(MovementEndReason.MapChanged);
        leaving.IsVisible = leaving.IsSelectable = leaving.CanMove = leaving.CanCast = false;
        if (isDeactivated)
        {
            var inactive = deactivatedStates[leaving];
            deactivatedStates[leaving] = (inactive.Active, false, false);
        }
        controlPath.RemoveLast();
        RestoreCurrent();
        ControlVersion++;
        SpatialChanged?.Invoke(this);
        ControlledObject?.NotifyVisibilityChanged();
        if (leaving.MapRuntime?.IsTemporary == true) leaving.MapRuntime.RemoveObject(leaving);
        return ControlledObject!;
    }

    private void RestoreCurrent()
    {
        if (ControlledObject is not { } body || !savedStates.Remove(body, out var state)) return;
        body.IsVisible = state.Visible;
        body.IsInteractable = state.Interactable;
        body.IsSelectable = state.Selectable;
        if (isDeactivated)
        {
            var inactive = deactivatedStates[body];
            deactivatedStates[body] = (inactive.Active, state.Move, state.Cast);
        }
        else
        {
            body.CanMove = body.IsActive && state.Move;
            body.CanCast = body.IsActive && state.Cast;
        }
    }

    public void Deactivate()
    {
        isDeactivated = true;
        foreach (var body in objects.Values)
        {
            deactivatedStates.TryAdd(body, (body.IsActive, body.CanMove, body.CanCast));
            body.Movement?.Stop(MovementEndReason.Stopped);
            body.MapRuntime?.UnitGrid?.Cancel(body);
            body.IsActive = body.CanMove = body.CanCast = false;
        }
    }

    public void Reactivate()
    {
        if (!isDeactivated) return;
        MapObject[] bodies = objects.Values.ToArray();
        foreach (var body in bodies)
        {
            var state = deactivatedStates[body];
            bool active = state.Active && !inactiveParallelObjects.Contains(body);
            using (body.SuspendVisibilityNotifications())
            {
                body.IsActive = active;
                body.CanMove = state.Move;
                body.CanCast = state.Cast;
            }
        }
        deactivatedStates.Clear();
        isDeactivated = false;
        foreach (var body in bodies) body.NotifyVisibilityChanged();
        NotifyVisibilityChanged();
    }

    public void Clear()
    {
        Deactivate();
        foreach (var body in objects.Values.ToArray()) body.MapRuntime?.RemoveUnitObjectForCleanup(body);
        objects.Clear();
        controlPath.Clear();
        inactiveParallelObjects.Clear();
        savedStates.Clear();
        deactivatedStates.Clear();
        isDeactivated = false;
        ControlVersion++;
        SpatialChanged?.Invoke(this);
    }
}
