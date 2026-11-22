using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Visibility;

public interface IMapVisibilityQuery
{
    bool IsVisible(string teamId, MapObject target);
    bool IsPositionVisible(string teamId, MapRuntime map, Vector2D position, double stealth = 0);
    IReadOnlyList<MapVisibilityInterval> TraceVisibility(string teamId, MapObject target,
        MapObject movingBody, Vector2D from, Vector2D to);
    IReadOnlyList<MapObject> GetVisibleObjects(string teamId, MapRuntime map);
}
