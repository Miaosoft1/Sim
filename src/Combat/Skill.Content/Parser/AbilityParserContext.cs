using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

public sealed class AbilityParserContext
{
    private readonly ContentSectionRegistry sections;
    private readonly ContentReadContext source;
    private readonly TriggerConditionSchemas conditions;

    internal AbilityParserContext(
        ContentSectionRegistry sections, ContentReadContext source, ExpressionSchema<CombatFormulaContext> formulas,
        ExpressionSchema<PropertyFormulaContext> propertyFormulas, PropertyTargetRegistration propertyTargets,
        TriggerConditionSchemas conditions)
    {
        this.sections = sections;
        this.source = source;
        CombatFormulas = formulas;
        PropertyFormulas = propertyFormulas;
        PropertyTargets = propertyTargets;
        this.conditions = conditions;
    }

    public string SourceFile => source.SourceFile;
    public ExpressionSchema<CombatFormulaContext> CombatFormulas { get; }
    public ExpressionSchema<PropertyFormulaContext> PropertyFormulas { get; }
    public PropertyTargetRegistration PropertyTargets { get; }
    public T Deserialize<T>(string yaml) => source.Deserialize<T>(yaml);
    public T RequireSection<T>(string name) where T : class => sections.Get<T>(name);
    public void RequireReference(string kind, string id) => source.RequireReference(kind, id);
    public CompiledExpression<TriggerConditionContext> CompileTriggerNumber(TriggerType type, string expression) =>
        StrictExpressionCompiler.Compile(expression, conditions.For(type), ExpressionValueType.Number);
    public Model.AbilityTriggerDefinition CompileTrigger(AbilityTriggerDto trigger, string id) =>
        AbilityContentModule.CompileTrigger(trigger, id, source, conditions);
}
