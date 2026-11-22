using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Ability;

public sealed class PropertyAbility : AbilityBase
{
    private readonly PropertyModifierProjection projection = new();
    public List<PropertyModifier> Modifiers { get; set; } = [];
    public PropertyRecalculationPolicy RecalculateOn { get; init; } = PropertyRecalculationPolicy.ProjectionChanged;

    internal bool ApplyModifiers(AtomicTransaction transaction, PropertyFormulaContext context, bool newlyRegistered) =>
        projection.Apply(transaction, Modifiers, context, 1, FormulaPolicy, RecalculateOn, newlyRegistered);
}
