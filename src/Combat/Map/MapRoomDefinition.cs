using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapRoomDefinition
    {
        public MapRoomDefinition(
            string id,
            string name,
            string roomType,
            double x,
            double y,
            double width,
            double height,
            bool isEntrance = false,
            bool isExit = false,
            string? description = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));

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
            RoomType = string.IsNullOrWhiteSpace(roomType) ? "Custom" : roomType;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            IsEntrance = isEntrance;
            IsExit = isExit;
            Description = description ?? string.Empty;
        }

        public string Id { get; }
        public string Name { get; }
        public string RoomType { get; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public bool IsEntrance { get; }
        public bool IsExit { get; }
        public string Description { get; }

        public Vector2D Center => new(X + Width / 2.0, Y + Height / 2.0);
        public Vector2D InnerEntrancePosition => new(X + Math.Min(3.0, Width * 0.3), Y + Height / 2.0);
        public Vector2D ExitDoorPosition => new(X + 1.0, Y + Height / 2.0);

        public bool Contains(Vector2D point)
        {
            return point.X >= X && point.X <= X + Width &&
                   point.Y >= Y && point.Y <= Y + Height;
        }

        public bool IsNearExit(Vector2D point, double radius = 5.0)
        {
            if (!IsExit) return false;
            return point.DistanceTo(Center) <= radius || Contains(point);
        }

        public bool IsNearExitDoor(Vector2D point, double radius = 4.0)
        {
            if (!IsExit) return false;
            return point.DistanceTo(ExitDoorPosition) <= radius ||
                   point.DistanceTo(InnerEntrancePosition) <= radius;
        }
    }
}
