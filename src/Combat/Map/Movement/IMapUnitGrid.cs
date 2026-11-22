using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public interface IMapUnitGrid
{
    double ToCellsPerSecond(double moveSpeed);
    bool TryFindPath(MapObject body, Vector2D destination, out IReadOnlyList<Vector2D> path);
    bool TryReserve(MapObject body, Vector2D destination);
    bool TryCommit(MapObject body);
    void Cancel(MapObject body);
    void ValidateAdd(MapObject body);
    void ValidatePosition(MapObject body, Vector2D position);
    void OnRemoved(MapObject body);
}
