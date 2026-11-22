using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public enum MapTargetingDistanceUnit
{
    World,
    Hex
}

public interface IMapTargetingMetric
{
    MapTargetingDistanceUnit DistanceUnit { get; }
    bool Contains(Vector2D position);
    double Distance(Vector2D first, Vector2D second);
}
