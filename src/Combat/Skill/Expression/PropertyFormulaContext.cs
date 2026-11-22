using Miaosoft.Sim.Combat.Map.Visibility;

namespace Miaosoft.Sim.Combat.Skill.Expression;

public sealed record PropertyFormulaContext(
    Unit? Source, UnitProperty TargetProperty, UnitVisibilityStats? Visibility, int AbilityLevel, Unit? Target = null)
{
    public UnitProperty? SourceProperty =>
        Source == null ? null : ReferenceEquals(Source, Target) ? TargetProperty : Source.Property;
}
