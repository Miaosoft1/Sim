namespace Miaosoft.Sim.Combat.Map.Visibility;

public interface IScopedMapVisibilityRule : IMapVisibilityRule
{
    /// <summary>Returns a conservative set of every viewer/target ID pair this rule can override.</summary>
    IReadOnlyCollection<(string ViewerId, string TargetId)> GetAffectedPairs();
}
