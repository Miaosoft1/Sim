using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill;

internal sealed class PropertyModifierProjection
{
    private sealed record Evaluation(
        Guid? SourceId, Guid? TargetId, int AbilityLevel, int TargetLevel,
        PropertyModifier[] Modifiers, FormulaEvaluationResult[] Values, FormulaEvaluationPolicy Policy);

    private readonly AtomicCell<Evaluation?> evaluation = new(null);

    internal bool Apply(
        AtomicTransaction transaction, IReadOnlyList<PropertyModifier> modifiers, PropertyFormulaContext context,
        int stack, FormulaEvaluationPolicy policy, PropertyRecalculationPolicy recalculateOn, bool newlyRegistered)
    {
        const PropertyRecalculationPolicy valid = PropertyRecalculationPolicy.ProjectionChanged |
            PropertyRecalculationPolicy.AbilityLevelChanged |
            PropertyRecalculationPolicy.TargetLevelChanged;
        if ((recalculateOn & ~valid) != 0)
            throw new InvalidOperationException("The property recalculation policy contains an unknown value.");
        if (stack < 1)
            throw new InvalidOperationException("A property modifier requires a positive stack count.");
        Evaluation? previous = transaction.Read(evaluation);
        Guid? sourceId = context.Source?.Id;
        Guid? targetId = context.Target?.Id;
        int targetLevel = context.TargetProperty.Level;
        bool refresh = newlyRegistered || previous == null ||
            previous.SourceId != sourceId || previous.TargetId != targetId ||
            !ReferenceEquals(previous.Policy, policy) || !previous.Modifiers.SequenceEqual(modifiers) ||
            recalculateOn.HasFlag(PropertyRecalculationPolicy.ProjectionChanged) ||
            recalculateOn.HasFlag(PropertyRecalculationPolicy.AbilityLevelChanged) &&
                previous.AbilityLevel != context.AbilityLevel ||
            recalculateOn.HasFlag(PropertyRecalculationPolicy.TargetLevelChanged) && previous.TargetLevel != targetLevel;
        bool retain = !recalculateOn.HasFlag(PropertyRecalculationPolicy.ProjectionChanged);
        var values = refresh ? retain ? new FormulaEvaluationResult[modifiers.Count] : [] : previous!.Values;
        bool usedFallback = false;
        for (int index = 0; index < modifiers.Count; index++)
        {
            PropertyModifier modifier = modifiers[index];
            FormulaEvaluationResult result = refresh
                ? policy.Evaluate(modifier.Formula, context, value => modifier.Target.IsRepresentable(value * stack))
                : values[index];
            double amount = result.Value * stack;
            if (!modifier.Target.IsRepresentable(amount))
                throw new ExpressionEvaluationException("The retained property modifier is outside its representable range.");
            modifier.Target.Apply(context, amount);
            if (refresh && retain) values[index] = result;
            usedFallback |= result.UsedFallback;
        }
        if (refresh && retain)
            transaction.Update(evaluation, _ => new Evaluation(
                sourceId, targetId, context.AbilityLevel, targetLevel,
                modifiers.ToArray(), values, policy));
        return usedFallback;
    }
}
