using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapObject
    {
        private bool isVisible = true, isActive = true, isInteractable = true, isSelectable = true;
        private bool canMove = true, canCast = true;
        private Vector2D position;
        private MapRuntime? mapRuntime;
        private bool publishesVisibilityChanges = true;
        public MapObject(
            string id,
            Vector2D position,
            EventReceiver eventReceiver,
            double collisionRadius = 0,
            MapObjectKind kind = MapObjectKind.Player,
            string? teamId = null,
            string? ownerId = null,
            Vector2D? size = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(eventReceiver);
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (collisionRadius < 0 || !double.IsFinite(collisionRadius))
            {
                throw new ArgumentOutOfRangeException(nameof(collisionRadius));
            }

            if (teamId is not null && string.IsNullOrWhiteSpace(teamId))
            {
                throw new ArgumentException("Team ID cannot be empty or whitespace.", nameof(teamId));
            }

            if (ownerId is not null && string.IsNullOrWhiteSpace(ownerId))
            {
                throw new ArgumentException("Owner ID cannot be empty or whitespace.", nameof(ownerId));
            }

            Vector2D objectSize = size ?? new Vector2D(1, 1);
            if (objectSize.X <= 0 || objectSize.Y <= 0 ||
                !double.IsFinite(objectSize.X) || !double.IsFinite(objectSize.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            Id = id;
            this.position = position;
            EventReceiver = eventReceiver;
            CollisionRadius = collisionRadius;
            Kind = kind;
            TeamId = teamId;
            OwnerId = ownerId;
            Size = objectSize;
        }

        public string Id { get; }
        public UnitMapObjectManager? Manager { get; internal set; }
        public bool IsVisible { get => isVisible; internal set => SetVisibilityState(ref isVisible, value); }
        public bool IsInteractable { get => isInteractable; internal set => SetVisibilityState(ref isInteractable, value); }
        public bool IsSelectable { get => isSelectable; internal set => SetVisibilityState(ref isSelectable, value); }
        public bool CanMove { get => canMove; internal set => SetVisibilityState(ref canMove, value); }
        public bool CanCast { get => canCast; internal set => SetVisibilityState(ref canCast, value); }
        public bool HasAppearanceOverlay { get; internal set; }
        public bool IsActive { get => isActive; set => SetVisibilityState(ref isActive, value); }
        public TimeSpan MovementInterval { get; set; } = MovementTiming.DefaultInterval;
        public MovementState? Movement { get; set; }
        public Vector2D Position
        {
            get => position;
            set
            {
                if (position == value) return;
                if (!double.IsFinite(value.X) || !double.IsFinite(value.Y))
                    throw new ArgumentOutOfRangeException(nameof(value));
                MapRuntime?.UnitGrid?.ValidatePosition(this, value);
                MapRuntime?.MoveSpatialObject(this, value);
                position = value;
                Manager?.NotifySpatialChanged(this);
                NotifyVisibilityChanged();
            }
        }

        internal void SetUnplacedPosition(Vector2D value)
        {
            if (MapRuntime != null)
                throw new InvalidOperationException("Only an unplaced object can change position without notification.");
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y))
                throw new ArgumentOutOfRangeException(nameof(value));
            position = value;
        }

        public EventReceiver EventReceiver { get; }
        public double CollisionRadius { get; }
        public MapObjectKind Kind { get; }
        public string? TeamId { get; }
        public string? OwnerId { get; }
        public Vector2D Size { get; }
        public string? CurrentDungeonId { get; internal set; }
        public Vector2D? InteriorPosition => CurrentDungeonId == null ? null : Position;
        public Vector2D? PositionBeforeEnteringDungeon { get; internal set; }
        public MapRuntime? MapRuntime
        {
            get => mapRuntime;
            internal set
            {
                if (mapRuntime == value) return;
                MapRuntime? previous = mapRuntime;
                previous?.RemoveSpatialObject(this);
                mapRuntime = value;
                value?.AddSpatialObject(this);
            }
        }

        private void SetVisibilityState(ref bool field, bool value)
        {
            if (field == value) return;
            field = value;
            NotifyVisibilityChanged();
        }

        public void NotifyVisibilityChanged(bool refreshRules = false)
        {
            if (publishesVisibilityChanges)
                MapRuntime?.NotifyVisibilityChanged(this, refreshRules);
        }

        internal IDisposable SuspendVisibilityNotifications() => new VisibilityNotificationLease(this);

        private sealed class VisibilityNotificationLease : IDisposable
        {
            private MapObject? owner;
            private readonly bool previous;

            public VisibilityNotificationLease(MapObject owner)
            {
                this.owner = owner;
                previous = owner.publishesVisibilityChanges;
                owner.publishesVisibilityChanges = false;
            }

            public void Dispose()
            {
                MapObject? body = Interlocked.Exchange(ref owner, null);
                if (body != null) body.publishesVisibilityChanges = previous;
            }
        }

        internal MapObject CopyForVisibility()
        {
            var copy = (MapObject)MemberwiseClone();
            copy.Manager = null;
            copy.publishesVisibilityChanges = false;
            return copy;
        }
    }
}
