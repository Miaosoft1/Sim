namespace Miaosoft.Sim.Combat.Map.Visibility;

public interface IVisibilityInvalidatingEvent
{
    bool RefreshVisibilityRules => false;
}
