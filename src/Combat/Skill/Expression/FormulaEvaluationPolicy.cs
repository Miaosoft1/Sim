namespace Miaosoft.Sim.Combat.Skill.Expression;

public sealed class FormulaEvaluationPolicy
{
    public static FormulaEvaluationPolicy Strict { get; } = new(FormulaEvaluationMode.Strict);

    private readonly Action<FormulaDiagnostic>? report;

    public FormulaEvaluationPolicy(FormulaEvaluationMode mode, Action<FormulaDiagnostic>? report = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == FormulaEvaluationMode.Production && report == null)
            throw new ArgumentException("Production evaluation requires a diagnostic reporter.", nameof(report));
        Mode = mode;
        this.report = report;
    }

    public FormulaEvaluationMode Mode { get; }

    public FormulaEvaluationResult Evaluate<TContext>(
        NumericFormula<TContext> formula, TContext context, Func<double, bool>? isRepresentable = null)
    {
        ArgumentNullException.ThrowIfNull(formula);
        return formula.Evaluate(context, Mode, report, isRepresentable);
    }
}
