using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public interface IMapMovement
{
    double Distance(Vector2D from, Vector2D to);
    Vector2D MoveTowards(Vector2D from, Vector2D to, double distance);
    (Vector2D Position, bool Blocked) Clip(Vector2D from, Vector2D to);
    IReadOnlyList<Vector2D>? FindPath(Vector2D from, Vector2D to);
}
