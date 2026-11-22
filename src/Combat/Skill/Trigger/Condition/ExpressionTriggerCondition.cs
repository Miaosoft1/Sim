using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class ExpressionTriggerCondition : ITriggerCondition
{
    private readonly CompiledExpression<TriggerConditionContext> expression;

    public ExpressionTriggerCondition(CompiledExpression<TriggerConditionContext> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Type != ExpressionValueType.Boolean)
            throw new ArgumentException("A trigger expression must be boolean.", nameof(expression));
        this.expression = expression;
    }

    public bool? Evaluate(TriggerConditionContext context) => expression.EvaluateCondition(context);
}
