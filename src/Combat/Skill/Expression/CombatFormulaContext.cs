namespace Miaosoft.Sim.Combat.Skill.Expression;

public sealed record CombatFormulaContext(Unit? Source, Unit? Target, double? Raw, int AbilityLevel)
{
    public UnitProperty? SourceProperty => Source?.Property;
    public UnitProperty? TargetProperty => Target?.Property;
}
