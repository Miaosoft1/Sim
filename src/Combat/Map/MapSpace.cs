using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public sealed class MapSpace
{
    public static MapSpace Planar { get; } = new(false, 0, 0);

    private MapSpace(bool isWrapped, double width, double height)
    {
        IsWrapped = isWrapped;
        Width = width;
        Height = height;
    }

    public bool IsWrapped { get; }
    public double Width { get; }
    public double Height { get; }

    public static MapSpace Toroidal(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        return new(true, width, height);
    }

    public static MapSpace ForBoundary(string? boundaryType, bool isInterior, double width, double height,
        bool hasIndependentBounds = false) =>
        !hasIndependentBounds && !isInterior &&
        (string.Equals(boundaryType, "Torus", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(boundaryType, "Spherical", StringComparison.OrdinalIgnoreCase))
            ? Toroidal(width, height)
            : Planar;

    public Vector2D Normalize(Vector2D point) => IsWrapped
        ? new(Vector2D.Wrap(point.X, Width), Vector2D.Wrap(point.Y, Height))
        : point;

    public Vector2D Delta(Vector2D from, Vector2D to) => IsWrapped
        ? Vector2D.ShortestDeltaOnTorus(Normalize(from), Normalize(to), Width, Height)
        : new(to.X - from.X, to.Y - from.Y);

    public double Distance(Vector2D from, Vector2D to)
    {
        Vector2D delta = Delta(from, to);
        return Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
    }

    public Vector2D MoveTowards(Vector2D from, Vector2D to, double distance) => IsWrapped
        ? Vector2D.MoveTowardsOnTorus(Normalize(from), Normalize(to), distance, Width, Height)
        : from.MoveTowards(to, distance);
}
