using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Parser;
using Miaosoft.Sim.Loadout.Content.Talents.Dto;
using Miaosoft.Sim.Loadout.Talents.Ability;

namespace Miaosoft.Sim.Loadout.Content.Talents.Parser;

internal sealed class TalentGrowthAbilityParser(IDictionary<string, (string TreeId, string RuleId)> bindings) : IHandlerParser
{
    public string Type => "TalentGrowth";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type == TriggerType.None)
            throw new InvalidDataException("TalentGrowth requires an observing trigger.");
        TalentGrowthAbilityDto data = context.Deserialize<TalentGrowthAbilityDto>(
            yaml ?? throw new InvalidDataException("TalentGrowth requires data."))
            ?? throw new InvalidDataException("TalentGrowth requires data.");
        if (string.IsNullOrWhiteSpace(data.TreeId) || data.TreeId != data.TreeId.Trim() ||
            string.IsNullOrWhiteSpace(data.RuleId) || data.RuleId != data.RuleId.Trim())
            throw new InvalidDataException("TalentGrowth requires normalized treeId and ruleId.");
        string treeId = data.TreeId;
        string ruleId = data.RuleId;
        var progress = context.CompileTriggerNumber(definition.Trigger.Type,
            data.Progress ?? throw new InvalidDataException("TalentGrowth progress must be a numeric expression."));
        bindings.Add(definition.Id, (treeId, ruleId));
        context.RequireReference("talentTree", treeId);
        context.RequireReference("talentGrowth", definition.Id);
        return new(definition, Type, (_, policy) => AbilityHandlerFactory.WithTrigger(new TalentGrowthAbility
        {
            Id = definition.Id, Name = definition.Name, Priority = definition.Priority,
            FormulaPolicy = policy, TreeId = treeId, RuleId = ruleId, Progress = progress
        }, definition));
    }
}
