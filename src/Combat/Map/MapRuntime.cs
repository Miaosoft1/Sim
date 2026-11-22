using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Spatial;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapRuntime
    {
        private readonly List<MapObject> objects = [];
        private readonly Dictionary<string, MapObject> objectsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MapRoom> roomsByDungeonId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MapRuntime> dungeonMapsById = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, MapRuntime> subMaps = [];
        private readonly MapVisibilityRuleRegistry visibilityRules = new();
        private readonly HashSet<IMapMovementStepLimiter> movementStepLimiters = new(ReferenceEqualityComparer.Instance);
        private readonly System.Collections.ObjectModel.ReadOnlyCollection<MapObject> objectView;
        private readonly System.Collections.ObjectModel.ReadOnlyDictionary<string, MapRuntime> dungeonMapView;
        private IMapClosingPolicy? closingPolicy;
        private SpatialGrid<MapObject>? spatial;

        public MapRuntime(double width = 100, double height = 100, string id = "map.main", string name = "MainMap",
            bool isMainPlane = true, IMapTargetingMetric? targetingMetric = null)
        {
            if (width <= 0 || !double.IsFinite(width))
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height <= 0 || !double.IsFinite(height))
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            Id = id;
            Name = name;
            Width = width;
            Height = height;
            IsMainPlane = isMainPlane;
            TargetingMetric = targetingMetric;
            Movement = new OverworldMovement();
            objectView = objects.AsReadOnly();
            dungeonMapView = new(dungeonMapsById);
            Root = this;
            visibilityRules.Changed += () => objects.FirstOrDefault()?.NotifyVisibilityChanged(refreshRules: true);
        }

        public MapRuntime(MapDefinition definition)
            : this(definition.Width, definition.Height, definition.Id, definition.Name, definition.IsMainPlane)
        {
            Definition = definition;
            Movement = string.Equals(definition.BoundaryType, "Bounded", StringComparison.OrdinalIgnoreCase)
                ? new BoundedMovement(new(Width / 2, Height / 2), Math.Min(Width, Height)) :
                definition.IsInterior ? new InteriorMovement(definition) :
                new OverworldMovement(definition.IsTorus, Width, Height);
        }

        public string Id { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public MapRuntime Root { get; private set; }
        public MapInstanceDirectory? Directory { get; private set; }
        public MapRuntime? EntryMap { get; private set; }
        public IMapMovement Movement { get; private set; }
        public IMapUnitGrid? UnitGrid { get; private set; }
        public IMapTargetingMetric? TargetingMetric { get; }
        public double TargetingDistance(Vector2D first, Vector2D second) =>
            TargetingMetric?.Distance(first, second) ?? Space.Distance(first, second);
        public MapSpace Space => Movement is OverworldMovement overworld ? overworld.Space : MapSpace.Planar;
        public bool IsTemporary { get; private set; }
        public bool IsMainPlane { get; private set; }
        public bool IsClosing { get; private set; }
        public IReadOnlyCollection<MapRuntime> SubMaps => Root.subMaps.Values;
        public IReadOnlyCollection<IMapVisibilityRule> VisibilityRules => visibilityRules.Rules;
        public string Name { get; }
        public MapDefinition? Definition { get; }
        public double Width { get; }
        public double Height { get; }
        public IReadOnlyList<MapObject> Objects => objectView;
        public IReadOnlyCollection<MapRoom> Rooms => roomsByDungeonId.Values;
        public IReadOnlyDictionary<string, MapRuntime> DungeonMaps => dungeonMapView;
        internal bool HasMovementStepLimiters => movementStepLimiters.Count != 0;
        internal void AssignDirectory(MapInstanceDirectory? directory) => Directory = directory;

        internal void ConfigureUnitGrid(IMapUnitGrid grid)
        {
            ArgumentNullException.ThrowIfNull(grid);
            if (UnitGrid != null || objects.Count != 0)
                throw new InvalidOperationException("A unit grid must be configured once before objects are added.");
            UnitGrid = grid;
        }

        private SpatialGrid<MapObject> Spatial => spatial ??= new(Space);

        public IReadOnlyList<MapObject> QuerySpatialCandidates(Vector2D minimum, Vector2D maximum) =>
            Spatial.QueryBox(minimum, maximum);

        public bool PrefersSpatialScan(Vector2D minimum, Vector2D maximum) =>
            Spatial.PrefersOrderedScan(minimum, maximum);

        internal void AddSpatialObject(MapObject body) => Spatial.Add(body, body.Position);
        internal void MoveSpatialObject(MapObject body, Vector2D position) => Spatial.Update(body, position);
        internal void RemoveSpatialObject(MapObject body)
        {
            if (!Spatial.Remove(body))
                throw new InvalidOperationException("The map object is missing from its spatial grid.");
        }

        public IDisposable RegisterVisibilityRule(IMapVisibilityRule rule) => visibilityRules.Register(rule);
        public event Action<MapObject, bool>? VisibilityChanged;

        internal void NotifyVisibilityChanged(MapObject body, bool refreshRules)
        {
            Root.VisibilityChanged?.Invoke(body, refreshRules);
            Directory?.NotifyVisibilityChanged(body, refreshRules);
        }

        public IDisposable RegisterMovementStepLimiter(IMapMovementStepLimiter limiter)
        {
            ArgumentNullException.ThrowIfNull(limiter);
            if (Directory != null) return Directory.RegisterMovementStepLimiter(limiter);
            MapRuntime root = Root;
            if (!root.movementStepLimiters.Add(limiter))
                throw new InvalidOperationException("The movement step limiter is already registered.");
            return new MovementStepLimiterLease(root, limiter);
        }

        internal double LimitMovementStep(MovementState movement, double maximumDistance)
        {
            double distance = maximumDistance;
            foreach (IMapMovementStepLimiter limiter in Root.movementStepLimiters)
            {
                double limited = limiter.LimitStep(movement, distance);
                if (!double.IsFinite(limited) || limited <= 0 || limited > distance)
                    throw new InvalidOperationException("A movement step limiter returned an invalid distance.");
                distance = limited;
            }
            return Directory?.LimitMovementStep(movement, distance) ?? distance;
        }

        private sealed class MovementStepLimiterLease(MapRuntime root, IMapMovementStepLimiter limiter) : IDisposable
        {
            private MapRuntime? owner = root;

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.movementStepLimiters.Remove(limiter);
            }
        }

        public void RegisterDungeonMap(string dungeonId, MapRuntime dungeonMap)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dungeonId);
            ArgumentNullException.ThrowIfNull(dungeonMap);
            if (dungeonMap.Directory != null ||
                dungeonMap == Root || dungeonMap.Root != dungeonMap ||
                dungeonMap.subMaps.ContainsKey(Root.InstanceId) || dungeonMapsById.ContainsKey(dungeonId))
                throw new InvalidOperationException("The dungeon map is already registered.");
            MapRuntime[] attached = [dungeonMap, .. dungeonMap.subMaps.Values];
            Directory?.ValidateRegistration(attached);
            dungeonMap.Root = Root;
            dungeonMap.EntryMap = this;
            dungeonMap.Root.subMaps.Add(dungeonMap.InstanceId, dungeonMap);
            foreach (var child in dungeonMap.subMaps.Values)
            {
                child.Root = Root;
                Root.subMaps.Add(child.InstanceId, child);
            }
            if (dungeonMap != Root) dungeonMap.subMaps.Clear();
            dungeonMapsById.Add(dungeonId, dungeonMap);
            Directory?.RegisterAttached(attached);
        }

        public MapRuntime? GetDungeonMap(string dungeonId)
        {
            return dungeonMapsById.GetValueOrDefault(dungeonId);
        }

        public bool EnterDungeon(MapObject player, string dungeonId)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(dungeonId);

            if (player.Kind != MapObjectKind.Player)
            {
                throw new ArgumentException("Only players can enter a dungeon.", nameof(player));
            }

            if (!dungeonMapsById.TryGetValue(dungeonId, out var dungeonMap))
                throw new InvalidOperationException($"Dungeon '{dungeonId}' has no target map.");
            if (player.MapRuntime == null) AddObject(player);
            if (!ReferenceEquals(player.MapRuntime, this))
                throw new InvalidOperationException("The player must belong to this map.");
            var manager = player.Manager;
            if (manager == null)
            {
                manager = new UnitMapObjectManager(player.Id);
                manager.Initialize(player);
            }
            if (manager.ControlledObject != player)
                throw new InvalidOperationException("Only the controlled object can enter a dungeon.");
            Vector2D entrance = dungeonMap.Definition?.FindEntranceRoom()?.InnerEntrancePosition ??
                new Vector2D(dungeonMap.Width / 2, dungeonMap.Height / 2);
            manager.Enter(dungeonMap, entrance, player.EventReceiver.Context?.ContextTimeSpan ?? TimeSpan.Zero,
                keepSourceVisible: dungeonMap.Definition?.KeepSourceVisible == true, dungeonId: dungeonId,
                appearanceOverlay: dungeonMap.Definition?.AppearanceOverlay == true);
            return true;
        }

        public bool ExitDungeon(MapObject player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (player.CurrentDungeonId is null)
            {
                return false;
            }

            var manager = player.Manager ?? throw new InvalidOperationException("The player has no map manager.");
            if (manager.ControlledObject != player || !manager.CanReturn ||
                !dungeonMapsById.TryGetValue(player.CurrentDungeonId, out var interior) ||
                interior != player.MapRuntime)
                throw new InvalidOperationException("The player is not in this map's dungeon.");
            manager.Return(player.EventReceiver.Context?.ContextTimeSpan ?? TimeSpan.Zero);
            return true;
        }

        public bool RemoveObject(MapObject mapObject)
        {
            return RemoveObjectCore(mapObject, unitCleanup: false);
        }

        internal bool RemoveUnitObjectForCleanup(MapObject mapObject)
        {
            return RemoveObjectCore(mapObject, unitCleanup: true);
        }

        private bool RemoveObjectCore(MapObject mapObject, bool unitCleanup)
        {
            ArgumentNullException.ThrowIfNull(mapObject);
            if (!objectsById.TryGetValue(mapObject.Id, out MapObject? registered) ||
                !ReferenceEquals(registered, mapObject))
            {
                return false;
            }
            if (!unitCleanup && IsTemporary && mapObject.Manager?.IsControlPathObject(mapObject) == true)
                throw new InvalidOperationException("A temporary map's control path must be resolved before removing its object.");

            UnitGrid?.OnRemoved(mapObject);
            objectsById.Remove(mapObject.Id);
            objects.Remove(mapObject);
            roomsByDungeonId.Remove(mapObject.Id);
            bool removedFromMap = ReferenceEquals(mapObject.MapRuntime, this);
            if (removedFromMap)
            {
                mapObject.MapRuntime = null;
            }
            if (mapObject.Manager != null) mapObject.IsActive = false;
            mapObject.Manager?.Removed(this, mapObject);
            if (removedFromMap) NotifyVisibilityChanged(mapObject, false);
            return true;
        }

        public void AddObject(MapObject mapObject)
        {
            ArgumentNullException.ThrowIfNull(mapObject);
            if (IsClosing || !Contains(mapObject.Position))
            {
                throw new ArgumentOutOfRangeException(nameof(mapObject), "Map object position must be inside the map.");
            }
            if (mapObject.MapRuntime != null && mapObject.MapRuntime != this)
                throw new InvalidOperationException("A map object cannot belong to multiple maps.");
            mapObject.Manager?.ValidateRegistration(this, mapObject);
            UnitGrid?.ValidateAdd(mapObject);

            if (!objectsById.TryAdd(mapObject.Id, mapObject))
            {
                throw new InvalidOperationException($"A map object with ID '{mapObject.Id}' already exists.");
            }

            mapObject.MapRuntime = this;
            objects.Add(mapObject);
            mapObject.Manager?.Registered(this, mapObject);
            mapObject.Manager?.NotifySpatialChanged(mapObject);
            mapObject.NotifyVisibilityChanged();
        }

        public MapObject? FindObject(string id) => objectsById.GetValueOrDefault(id);

        public bool Contains(Vector2D position) =>
            double.IsFinite(position.X) && double.IsFinite(position.Y) &&
            (Movement is BoundedMovement spirit ? spirit.Contains(position) :
                position.X >= 0 && position.X <= Width && position.Y >= 0 && position.Y <= Height);

        public MapRuntime CreateTemporaryMap(Vector2D center, bool isMainPlane, bool empty = false)
        {
            if (IsClosing) throw new InvalidOperationException("A closing map cannot create a temporary map.");
            var map = Definition == null || empty
                ? new MapRuntime(Width, Height, Id, Name, isMainPlane)
                : new MapRuntime(Definition);
            map.Root = Root;
            map.EntryMap = this;
            map.Movement = new BoundedMovement(center);
            map.IsTemporary = true;
            map.IsMainPlane = isMainPlane;
            Directory?.ValidateRegistration([map]);
            map.Root.subMaps.Add(map.InstanceId, map);
            Directory?.RegisterAttached([map]);
            return map;
        }

        public void Close()
        {
            Close(new MapCloseRequest("explicit", TimeSpan.Zero));
        }

        public void ConfigureClosingPolicy(IMapClosingPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (!IsTemporary || IsClosing || closingPolicy != null)
                throw new InvalidOperationException("A closing policy can only be configured once on an open temporary map.");
            closingPolicy = policy;
        }

        public void ValidateClose(MapCloseRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
            if (!IsTemporary) throw new InvalidOperationException("Only temporary maps can be closed.");
            if (IsClosing) throw new InvalidOperationException("The map is already closing.");
            if (closingPolicy == null) ValidateCloseAfterRemoving([]);
            else closingPolicy.Validate(this, request);
        }

        public void Close(MapCloseRequest request)
        {
            if (IsClosing) return;
            ValidateClose(request);
            closingPolicy?.Execute(this, request);
            ValidateCloseAfterRemoving([]);
            IsClosing = true;
            foreach (var body in objects.ToArray())
            {
                if (body.Kind != MapObjectKind.Projectile) body.Movement?.Stop(MovementEndReason.MapChanged);
                RemoveObject(body);
            }
            Root.subMaps.Remove(InstanceId);
            Directory?.UnregisterAttached(this);
        }

        public void ValidateCloseAfterRemoving(IReadOnlyCollection<MapObject> plannedRemovals,
            IReadOnlyCollection<MapRuntime>? plannedChildClosures = null)
        {
            ArgumentNullException.ThrowIfNull(plannedRemovals);
            if (!IsTemporary) throw new InvalidOperationException("Only temporary maps can be closed.");
            if (IsClosing) throw new InvalidOperationException("The map is already closing.");
            if (plannedRemovals.Any(body => body.MapRuntime != this))
                throw new InvalidOperationException("A planned removal does not belong to this map.");
            if (plannedChildClosures?.Any(child => child.EntryMap != this || child.IsClosing) == true)
                throw new InvalidOperationException("A planned child closure does not belong to this map.");
            if (Root.subMaps.Values.Any(map => ReferenceEquals(map.EntryMap, this) &&
                plannedChildClosures?.Contains(map) != true))
                throw new InvalidOperationException("The map still owns child maps; close them according to their policies first.");
            if (objects.Any(body => body.Manager?.IsControlPathObject(body) == true &&
                !plannedRemovals.Contains(body)))
                throw new InvalidOperationException("The map still contains a unit control path; execute its closing policy first.");
        }

        public void AddRoom(MapRoom room)
        {
            ArgumentNullException.ThrowIfNull(room);
            AddObject(room.Dungeon);
            roomsByDungeonId.Add(room.Dungeon.Id, room);
        }

        public bool InteractWithDungeon(MapObject player, MapRoom room)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(room);
            if (player.Kind != MapObjectKind.Player)
            {
                throw new ArgumentException("Only players can enter a dungeon.", nameof(player));
            }

            if (!objectsById.TryGetValue(player.Id, out MapObject? registeredPlayer) ||
                !ReferenceEquals(registeredPlayer, player) ||
                !roomsByDungeonId.TryGetValue(room.Dungeon.Id, out MapRoom? registeredRoom) ||
                !ReferenceEquals(registeredRoom, room))
            {
                throw new InvalidOperationException("The player and room must belong to this map.");
            }

            return EnterDungeon(player, room.Dungeon.Id);
        }

        public IReadOnlyList<MapObject> GetVisibleObjects(
            string teamId, IMapVisibilityQuery snapshot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
            ArgumentNullException.ThrowIfNull(snapshot);
            return snapshot.GetVisibleObjects(teamId, this);
        }
    }
}
