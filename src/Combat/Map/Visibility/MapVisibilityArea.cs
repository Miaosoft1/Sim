using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Visibility;

public sealed class MapVisibilityArea
{
    public static MapVisibilityArea Default { get; } =
        new(60.444444444444443, 49.184118419728712, new Vector2D(1, 1));

    public MapVisibilityArea(double width, double depth, Vector2D direction)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(depth) || depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) ||
            direction.X == 0 && direction.Y == 0)
            throw new ArgumentOutOfRangeException(nameof(direction));

        Width = width;
        Depth = depth;
        double scale = Math.Max(Math.Abs(direction.X), Math.Abs(direction.Y));
        double x = direction.X / scale, y = direction.Y / scale;
        double length = Math.Sqrt(x * x + y * y);
        Direction = new Vector2D(x / length, y / length);
    }

    public double Width { get; }
    public double Depth { get; }
    public Vector2D Direction { get; }
    public double DefaultVisionRange
    {
        get
        {
            double largest = Math.Max(Width, Depth);
            double ratio = Math.Min(Width, Depth) / largest;
            return largest / 2 * Math.Sqrt(1 + ratio * ratio);
        }
    }

    public bool Contains(MapRuntime? map, Vector2D origin, Vector2D target)
    {
        Vector2D delta = (map?.Space ?? MapSpace.Planar).Delta(origin, target);
        double dx = delta.X;
        double dy = delta.Y;

        double right = dx * Direction.Y - dy * Direction.X;
        double depth = dx * Direction.X + dy * Direction.Y;
        return Math.Abs(right) <= Width / 2 && Math.Abs(depth) <= Depth / 2;
    }
}
