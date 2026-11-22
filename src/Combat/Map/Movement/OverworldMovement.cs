using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public sealed class OverworldMovement : IMapMovement
{
    public OverworldMovement(bool wrap = false, double width = 0, double height = 0)
    {
        Space = wrap ? MapSpace.Toroidal(width, height) : MapSpace.Planar;
        Width = width;
        Height = height;
    }

    public MapSpace Space { get; }
    public bool Wrap => Space.IsWrapped;
    public double Width { get; }
    public double Height { get; }
    public double Distance(Vector2D from, Vector2D to) => Space.Distance(from, to);
    public Vector2D MoveTowards(Vector2D from, Vector2D to, double distance) => Space.MoveTowards(from, to, distance);
    public (Vector2D Position, bool Blocked) Clip(Vector2D from, Vector2D to) => (to, false);
    public IReadOnlyList<Vector2D> FindPath(Vector2D from, Vector2D to) => new[] { to };
}
