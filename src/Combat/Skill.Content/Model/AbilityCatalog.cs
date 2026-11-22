using System.Collections.ObjectModel;

namespace Miaosoft.Sim.Combat.Skill.Content.Model;

public sealed class AbilityCatalog
{
    private readonly IReadOnlyDictionary<string, AbilityHandlerDefinition> definitions;

    internal AbilityCatalog(IEnumerable<AbilityHandlerDefinition> definitions)
    {
        this.definitions = new ReadOnlyDictionary<string, AbilityHandlerDefinition>(
            definitions.ToDictionary(definition => definition.Content.Id, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, AbilityHandlerDefinition> Definitions => definitions;

    public AbilityHandlerDefinition Require(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return definitions.TryGetValue(id, out AbilityHandlerDefinition? definition)
            ? definition : throw new KeyNotFoundException($"Ability '{id}' is not declared.");
    }
}
