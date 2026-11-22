using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public sealed class InteriorMovement(MapDefinition definition) : IMapMovement
{
    public double Distance(Vector2D from, Vector2D to) => from.DistanceTo(to);
    public Vector2D MoveTowards(Vector2D from, Vector2D to, double distance) => from.MoveTowards(to, distance);
    public (Vector2D Position, bool Blocked) Clip(Vector2D from, Vector2D to) => definition.ClipMovement(from, to);
    public IReadOnlyList<Vector2D>? FindPath(Vector2D from, Vector2D to) => definition.FindPath(from, to);
}
