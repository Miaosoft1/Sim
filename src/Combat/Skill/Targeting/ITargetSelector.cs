namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    public interface ITargetSelector
    {
        TargetingOrigin Origin { get; set; }
        bool Validate(AbilityTargetingContext context);
        IReadOnlyList<Unit> ResolveTargets(AbilityTargetingContext context, UnitSpatialRegistry registry);
    }
}
