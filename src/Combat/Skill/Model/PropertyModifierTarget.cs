using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class PropertyModifierTarget
{
    private readonly Func<double, bool> isRepresentable;
    private readonly Action<PropertyFormulaContext, double> apply;

    public PropertyModifierTarget(
        string id, Func<double, bool> isRepresentable, Action<PropertyFormulaContext, double> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(isRepresentable);
        ArgumentNullException.ThrowIfNull(apply);
        Id = id;
        this.isRepresentable = isRepresentable;
        this.apply = apply;
    }

    public string Id { get; }
    internal bool IsRepresentable(double value) => double.IsFinite(value) && isRepresentable(value);
    internal void Apply(PropertyFormulaContext context, double value) => apply(context, value);
}
