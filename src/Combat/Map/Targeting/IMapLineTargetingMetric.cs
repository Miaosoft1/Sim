using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map;

public interface IMapLineTargetingMetric : IMapTargetingMetric
{
    double ToWorldLineLength(Vector2D direction, double range);
    double ToWorldLineWidth(double width);
}
