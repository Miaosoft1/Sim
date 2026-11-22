using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill
{
    public readonly record struct DamageResult(string DamageType, int ScaledValue, bool UsedFallback);

    /// <summary>
    /// Definition of a damage output for an ability, evaluated via expression.
    /// Replaces legacy DamageFactor.
    /// </summary>
    public class DamageDefinition
    {
        public DamageTypeRegistry DamageTypes { get; }
        public string DamageType { get; }
        public NumericFormula<CombatFormulaContext> Formula { get; }
        public int Priority { get; }

        public DamageDefinition(
            DamageTypeRegistry damageTypes, string damageType,
            NumericFormula<CombatFormulaContext> formula, int priority = 0)
        {
            ArgumentNullException.ThrowIfNull(damageTypes);
            ArgumentNullException.ThrowIfNull(formula);
            DamageTypes = damageTypes;
            DamageType = damageTypes.Require(damageType).Id;
            Formula = formula;
            Priority = priority;
        }

        public IReadOnlyList<DamageResult> Calculate(Damage damage, FormulaEvaluationPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(damage);
            ArgumentNullException.ThrowIfNull(policy);
            var ctx = new CombatFormulaContext(
                damage.Source, damage.Target, damage.GetValueOrDefault(DamageType) / (double)Damage.ZOOM,
                damage.Handler.Level);
            FormulaEvaluationResult evaluated = policy.Evaluate(Formula, ctx, Damage.IsRepresentable);
            return [new DamageResult(DamageType, Damage.Scale(evaluated.Value), evaluated.UsedFallback)];
        }
    }
}
