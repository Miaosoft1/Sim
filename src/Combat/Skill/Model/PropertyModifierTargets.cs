using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill;

public static class PropertyModifierTargets
{
    public static PropertyModifierTarget LifePointMax { get; } =
        Integer("lifePointMax", (property, delta) => property.LifePointMax = checked(property.LifePointMax + delta));

    public static PropertyModifierTarget EnergyMax { get; } =
        Integer("energyMax", (property, delta) => property.EnergyMax = checked(property.EnergyMax + delta));

    public static PropertyModifierTarget MoveSpeedBase { get; } =
        Integer("moveSpeedBase", (property, delta) => property.MoveSpeedBase = checked((property.MoveSpeedBase ?? 0) + delta));

    public static PropertyModifierTarget MoveSpeed { get; } =
        Integer("moveSpeed", (property, delta) => property.MoveSpeed = checked((property.MoveSpeed ?? 0) + delta));

    public static PropertyModifierTarget ExperienceMax { get; } =
        Integer("experienceMax", (property, delta) =>
        {
            property.ExperienceMax = Math.Max(0, checked(property.ExperienceMax + delta));
            property.Experience = Math.Clamp(property.Experience, 0, property.ExperienceMax);
        });

    public static PropertyModifierTarget VisionRange { get; } =
        Visibility("visionRange", (visibility, value) => visibility.AddVisionRange(value));

    public static PropertyModifierTarget Stealth { get; } =
        Visibility("stealth", (visibility, value) => visibility.AddStealth(value));

    private static PropertyModifierTarget Integer(string id, Action<UnitProperty, int> apply) =>
        new(id, value =>
        {
            double rounded = Math.Round(value, MidpointRounding.ToEven);
            return rounded >= int.MinValue && rounded <= int.MaxValue;
        }, (context, value) => apply(context.TargetProperty, checked((int)Math.Round(value, MidpointRounding.ToEven))));

    private static PropertyModifierTarget Visibility(string id, Action<UnitVisibilityStats, double> apply) =>
        new(id, double.IsFinite, (context, value) =>
            apply(context.Visibility ?? throw new InvalidOperationException("Visibility statistics are required."), value));
}
