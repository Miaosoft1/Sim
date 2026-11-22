using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Loadout.Content.Talents.Dto;
using Miaosoft.Sim.Loadout.Content.Talents.Model;
using Miaosoft.Sim.Loadout.Content.Talents.Parser;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Content.Registration;

internal sealed class TalentContentModule(TalentTreeParser parser) : IContentModule
{
    public string Id => LoadoutContentServiceCollectionExtensions.TalentModuleId;

    public void Register(ContentSectionRegistry registry)
    {
        var authored = new Dictionary<string, (TalentTreeDto Dto, ContentReadContext Source)>(StringComparer.Ordinal);
        var actualGrowth = new Dictionary<string, (string TreeId, string RuleId)>(StringComparer.Ordinal);
        var expectedGrowth = new Dictionary<string, (string TreeId, string RuleId)>(StringComparer.Ordinal);
        registry.AbilityParsers().Register(new TalentGrowthAbilityParser(actualGrowth));
        registry.RegisterReferenceResolver("talentTree",
            id => registry.Get<TalentTreeCatalog>("talentTree").Definitions.ContainsKey(id));
        registry.RegisterReferenceResolver("talentGrowth", id =>
            expectedGrowth.TryGetValue(id, out var expected) &&
            actualGrowth.TryGetValue(id, out var actual) && actual == expected);
        registry.RegisterCollection("talentTree", (yaml, source) =>
        {
            List<TalentTreeDto> trees = source.Deserialize<List<TalentTreeDto>>(yaml)
                ?? throw new InvalidDataException($"{source.SourceFile}: talentTree must be a list.");
            foreach (TalentTreeDto dto in trees)
            {
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id))
                    throw new InvalidDataException($"{source.SourceFile}: talent trees require distinct nonempty IDs.");
                if (authored.TryGetValue(dto.Id, out var existing))
                    Merge(existing.Dto, dto, source.SourceFile);
                else authored.Add(dto.Id, (dto, source));
            }
        }, () =>
        {
            var definitions = new List<TalentTreeDefinition>(authored.Count);
            foreach ((TalentTreeDto dto, ContentReadContext source) in authored.Values)
            {
                try
                {
                    TalentTreeDefinition definition = parser.Compile(dto);
                    foreach (var member in definition.Nodes.Values.SelectMany(node => node.Abilities))
                        source.RequireReference("ability", member.AbilityId);
                    foreach (TalentGrowthRuleDefinition rule in definition.GrowthRules)
                    {
                        if (!expectedGrowth.TryAdd(rule.AbilityId, (definition.Id, rule.Id)))
                            throw new InvalidDataException($"Growth ability '{rule.AbilityId}' is bound to more than one rule.");
                        source.RequireReference("ability", rule.AbilityId);
                        source.RequireReference("talentGrowth", rule.AbilityId);
                    }
                    definitions.Add(definition);
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException)
                {
                    throw new InvalidDataException($"{source.SourceFile}: talent tree '{dto.Id}': {error.Message}", error);
                }
            }
            return new TalentTreeCatalog(definitions);
        });
    }

    private static void Merge(TalentTreeDto target, TalentTreeDto fragment, string sourceFile)
    {
        if (!string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(fragment.Name) &&
            !string.Equals(target.Name, fragment.Name, StringComparison.Ordinal))
            throw new InvalidDataException($"{sourceFile}: talent tree '{target.Id}' has conflicting names.");
        if (target.InitialPoints.HasValue && fragment.InitialPoints.HasValue &&
            target.InitialPoints != fragment.InitialPoints)
            throw new InvalidDataException($"{sourceFile}: talent tree '{target.Id}' has conflicting initial points.");
        if (string.IsNullOrWhiteSpace(target.Name)) target.Name = fragment.Name;
        target.InitialPoints ??= fragment.InitialPoints;
        target.Branches ??= [];
        target.GrowthRules ??= [];
        if (fragment.Branches != null) target.Branches.AddRange(fragment.Branches);
        if (fragment.GrowthRules != null) target.GrowthRules.AddRange(fragment.GrowthRules);
    }
}
