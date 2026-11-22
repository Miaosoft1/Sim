using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class ExperienceRewardAbilityParser : IHandlerParser
{
    public string Type => "ExperienceReward";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type != TriggerType.Death ||
            definition.Trigger.DeathPhase != DeathTriggerPhase.Confirmed)
            throw new InvalidDataException("ExperienceReward requires a confirmed Death trigger.");
        ExperienceRewardAbilityDto data = context.Deserialize<ExperienceRewardAbilityDto>(
            yaml ?? throw new InvalidDataException("ExperienceReward requires data."))
            ?? throw new InvalidDataException("ExperienceReward requires data.");
        if (data.Amount is null or < 0 || data.VictimTags == null || data.KillerTags == null ||
            data.VictimTags.Any(string.IsNullOrWhiteSpace) || data.KillerTags.Any(string.IsNullOrWhiteSpace) ||
            data.VictimTags.Distinct(StringComparer.Ordinal).Count() != data.VictimTags.Count ||
            data.KillerTags.Distinct(StringComparer.Ordinal).Count() != data.KillerTags.Count)
            throw new InvalidDataException("ExperienceReward requires a non-negative amount and valid tag filters.");

        int amount = data.Amount.Value;
        string[] victimTags = data.VictimTags.ToArray();
        string[] killerTags = data.KillerTags.ToArray();
        return new AbilityHandlerDefinition(definition, Type, (_, _) =>
            AbilityHandlerFactory.WithTrigger(new ExperienceRewardAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                Amount = amount,
                RequiredVictimTags = Array.AsReadOnly(victimTags),
                RequiredKillerTags = Array.AsReadOnly(killerTags)
            }, definition));
    }
}
