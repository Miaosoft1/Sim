using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public sealed class BoundedMovement : IMapMovement
{
    public BoundedMovement(Vector2D center, double sideLength = 68)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
            !double.IsFinite(sideLength) || sideLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(sideLength));
        Center = center;
        SideLength = sideLength;
    }

    public Vector2D Center { get; }
    public double SideLength { get; }
    public Vector2D Min => new(Center.X - SideLength / 2, Center.Y - SideLength / 2);
    public Vector2D Max => new(Center.X + SideLength / 2, Center.Y + SideLength / 2);
    public Vector2D GetInsetEdgeMidpoint(int edge, double inset)
    {
        if (edge < 0 || edge >= 4) throw new ArgumentOutOfRangeException(nameof(edge));
        if (!double.IsFinite(inset) || inset < 0 || inset > SideLength / 2)
            throw new ArgumentOutOfRangeException(nameof(inset));
        return edge switch
        {
            0 => new(Min.X + inset, Center.Y),
            1 => new(Max.X - inset, Center.Y),
            2 => new(Center.X, Min.Y + inset),
            _ => new(Center.X, Max.Y - inset)
        };
    }

    public bool Contains(Vector2D point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;
    public double Distance(Vector2D from, Vector2D to) => from.DistanceTo(to);
    public Vector2D MoveTowards(Vector2D from, Vector2D to, double distance) => from.MoveTowards(to, distance);
    public (Vector2D Position, bool Blocked) Clip(Vector2D from, Vector2D to)
    {
        if (!Contains(from)) throw new ArgumentOutOfRangeException(nameof(from));
        double t = 1;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (to.X < Min.X) t = Math.Min(t, (Min.X - from.X) / dx);
        if (to.X > Max.X) t = Math.Min(t, (Max.X - from.X) / dx);
        if (to.Y < Min.Y) t = Math.Min(t, (Min.Y - from.Y) / dy);
        if (to.Y > Max.Y) t = Math.Min(t, (Max.Y - from.Y) / dy);
        return (new(from.X + dx * t, from.Y + dy * t), t < 1);
    }
    public IReadOnlyList<Vector2D>? FindPath(Vector2D from, Vector2D to) =>
        Contains(from) && Contains(to) ? new[] { to } : null;
}
