namespace Miaosoft.Sim.Combat.Skill;

[Flags]
public enum PropertyRecalculationPolicy
{
    None = 0,
    ProjectionChanged = 1,
    AbilityLevelChanged = 2,
    TargetLevelChanged = 4
}
