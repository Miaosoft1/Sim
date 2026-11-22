namespace Miaosoft.Sim.Combat.Map.Vector
{
    public readonly record struct Vector2D(double X, double Y)
    {
        public static readonly Vector2D Zero = new(0, 0);

        public double DistanceTo(Vector2D other)
        {
            double deltaX = other.X - X;
            double deltaY = other.Y - Y;
            return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        public Vector2D MoveTowards(Vector2D destination, double distance)
        {
            if (distance < 0 || !double.IsFinite(distance))
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }

            double deltaX = destination.X - X;
            double deltaY = destination.Y - Y;
            double remaining = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (remaining == 0 || distance >= remaining)
            {
                return destination;
            }

            double ratio = distance / remaining;
            return new Vector2D(X + deltaX * ratio, Y + deltaY * ratio);
        }

        public static double Wrap(double value, double max)
        {
            if (max <= 0) return value;
            double res = value % max;
            return res < 0 ? res + max : res;
        }

        public static Vector2D ShortestDeltaOnTorus(Vector2D from, Vector2D to, double width, double height)
        {
            double dx = to.X - from.X;
            if (width > 0 && Math.Abs(dx) > width / 2.0)
            {
                dx = dx > 0 ? dx - width : dx + width;
            }

            double dy = to.Y - from.Y;
            if (height > 0 && Math.Abs(dy) > height / 2.0)
            {
                dy = dy > 0 ? dy - height : dy + height;
            }

            return new Vector2D(dx, dy);
        }

        public static double DistanceOnTorus(Vector2D from, Vector2D to, double width, double height)
        {
            Vector2D delta = ShortestDeltaOnTorus(from, to, width, height);
            return Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        }

        public static Vector2D MoveTowardsOnTorus(Vector2D from, Vector2D to, double distance, double width, double height)
        {
            if (distance < 0 || !double.IsFinite(distance))
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }

            Vector2D delta = ShortestDeltaOnTorus(from, to, width, height);
            double remaining = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (remaining == 0 || distance >= remaining)
            {
                return new Vector2D(Wrap(to.X, width), Wrap(to.Y, height));
            }

            double ratio = distance / remaining;
            double nextX = Wrap(from.X + delta.X * ratio, width);
            double nextY = Wrap(from.Y + delta.Y * ratio, height);
            return new Vector2D(nextX, nextY);
        }
    }
}
