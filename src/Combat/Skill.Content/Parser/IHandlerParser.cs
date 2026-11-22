using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

public interface IHandlerParser
{
    string Type { get; }
    AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context);

    IReadOnlyList<IHandler> Parse(
        AbilityContentDefinition definition, string? yaml, AbilityParserContext context,
        int? level = null, FormulaEvaluationPolicy? policy = null) =>
        Compile(definition, yaml, context).CreateHandlers(level, policy);
}
