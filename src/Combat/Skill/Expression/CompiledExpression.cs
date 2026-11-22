namespace Miaosoft.Sim.Combat.Skill.Expression;

public sealed class CompiledExpression<TContext>
{
    private readonly BoundExpressionNode<TContext> root;

    internal CompiledExpression(BoundExpressionNode<TContext> root)
    {
        this.root = root;
    }

    public ExpressionValueType Type => root.Type
        ?? throw new InvalidOperationException("A compiled expression has no result type.");

    public double? EvaluateNumber(TContext context)
    {
        if (Type != ExpressionValueType.Number)
            throw new InvalidOperationException("The compiled expression is not numeric.");
        ExpressionValue result = root.Evaluate(context);
        return result.HasValue ? result.AsNumber : null;
    }

    public bool? EvaluateCondition(TContext context)
    {
        if (Type != ExpressionValueType.Boolean)
            throw new InvalidOperationException("The compiled expression is not a condition.");
        ExpressionValue result = root.Evaluate(context);
        return result.HasValue ? result.AsBoolean : null;
    }

    public bool Matches(TContext context) => EvaluateCondition(context) == true;
}

internal abstract class BoundExpressionNode<TContext>(ExpressionValueType? type)
{
    public ExpressionValueType? Type { get; } = type;
    public abstract ExpressionValue Evaluate(TContext context);
}
