namespace Miaosoft.Sim.Combat.Map;

public interface IMapMovementStepLimiter
{
    double LimitStep(MovementState movement, double maximumDistance);
}
