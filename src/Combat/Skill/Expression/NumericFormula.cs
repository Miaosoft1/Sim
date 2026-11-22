namespace Miaosoft.Sim.Combat.Skill.Expression;

public enum FormulaEvaluationMode
{
    Strict,
    Production
}

public readonly record struct FormulaEvaluationResult(double Value, bool UsedFallback);

public sealed record FormulaDiagnostic(string Formula, ExpressionEvaluationException Error);

public sealed class NumericFormula<TContext>
{
    private readonly string formula;
    private readonly CompiledExpression<TContext> primary;
    private readonly CompiledExpression<TContext>? fallback;

    private NumericFormula(
        string formula, CompiledExpression<TContext> primary, CompiledExpression<TContext>? fallback)
    {
        this.formula = formula;
        this.primary = primary;
        this.fallback = fallback;
    }

    public static NumericFormula<TContext> Compile(
        string formula, ExpressionSchema<TContext> schema, string? fallback = null)
    {
        CompiledExpression<TContext> primary =
            StrictExpressionCompiler.Compile(formula, schema, ExpressionValueType.Number);
        CompiledExpression<TContext>? recovery = fallback == null
            ? null
            : StrictExpressionCompiler.Compile(fallback, schema, ExpressionValueType.Number);
        return new NumericFormula<TContext>(formula, primary, recovery);
    }

    public FormulaEvaluationResult Evaluate(
        TContext context, FormulaEvaluationMode mode, Action<FormulaDiagnostic>? report = null,
        Func<double, bool>? isRepresentable = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        try
        {
            return new FormulaEvaluationResult(Required(primary, context, isRepresentable), false);
        }
        catch (ExpressionEvaluationException error) when (mode == FormulaEvaluationMode.Production && fallback != null)
        {
            if (report == null)
                throw new InvalidOperationException("Production fallback requires a diagnostic reporter.", error);
            report(new FormulaDiagnostic(formula, error));
            return new FormulaEvaluationResult(Required(fallback, context, isRepresentable), true);
        }
    }

    private static double Required(
        CompiledExpression<TContext> expression, TContext context, Func<double, bool>? isRepresentable)
    {
        double result = expression.EvaluateNumber(context)
            ?? throw new ExpressionEvaluationException("The required numeric formula result is missing.");
        if (isRepresentable != null && !isRepresentable(result))
            throw new ExpressionEvaluationException("The numeric formula result is outside its representable range.");
        return result;
    }
}
