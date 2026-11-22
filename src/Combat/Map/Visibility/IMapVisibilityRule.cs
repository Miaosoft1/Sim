namespace Miaosoft.Sim.Combat.Map.Visibility
{
    public interface IMapVisibilityRule
    {
        bool? GetVisibilityOverride(MapObject viewer, MapObject target);
    }
}
