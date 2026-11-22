using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapCorridorDefinition
    {
        public MapCorridorDefinition(
            string id,
            string fromRoomId,
            string toRoomId,
            double startX,
            double startY,
            double endX,
            double endY,
            double width = 3.0,
            string? name = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(fromRoomId);
            ArgumentException.ThrowIfNullOrWhiteSpace(toRoomId);
            if (!double.IsFinite(startX) || !double.IsFinite(startY) ||
                !double.IsFinite(endX) || !double.IsFinite(endY))
                throw new ArgumentOutOfRangeException(nameof(startX));

            if (width <= 0 || !double.IsFinite(width))
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            Id = id;
            Name = name ?? id;
            FromRoomId = fromRoomId;
            ToRoomId = toRoomId;
            StartX = startX;
            StartY = startY;
            EndX = endX;
            EndY = endY;
            Width = width;
        }

        public string Id { get; }
        public string Name { get; }
        public string FromRoomId { get; }
        public string ToRoomId { get; }
        public double StartX { get; }
        public double StartY { get; }
        public double EndX { get; }
        public double EndY { get; }
        public double Width { get; }

        public Vector2D Start => new(StartX, StartY);
        public Vector2D End => new(EndX, EndY);

        public bool Contains(Vector2D point)
        {
            double dx = EndX - StartX;
            double dy = EndY - StartY;
            double lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.000001)
            {
                return point.DistanceTo(Start) <= Width / 2.0;
            }

            double t = ((point.X - StartX) * dx + (point.Y - StartY) * dy) / lenSq;
            if (t < 0.0 || t > 1.0)
            {
                return false;
            }

            double projX = StartX + t * dx;
            double projY = StartY + t * dy;
            double distSq = (point.X - projX) * (point.X - projX) + (point.Y - projY) * (point.Y - projY);
            return distSq <= (Width / 2.0) * (Width / 2.0);
        }
    }
}
