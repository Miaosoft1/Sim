using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Loadout.Talents.Model;

public sealed class UnitTalents
{
    public UnitTalents(Unit owner, IEnumerable<TalentTreeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(definitions);
        Context context = owner.EventReceiver.Context
            ?? throw new InvalidOperationException("Bind the unit to its context before composing its talent feature.");
        TalentTreeDefinition[] selected = definitions.ToArray();
        if (selected.Any(definition => definition == null) ||
            selected.Select(definition => definition.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Select distinct non-null talent definitions for each owner.", nameof(definitions));
        Owner = owner;
        AvailableTrees = new ReadOnlyDictionary<string, TalentTreeInstance>(
            selected.ToDictionary(definition => definition.Id,
                definition => new TalentTreeInstance(this, definition, context), StringComparer.Ordinal));
    }

    public Unit Owner { get; }
    internal IReadOnlyDictionary<string, TalentTreeInstance> AvailableTrees { get; }
    internal AtomicCell<IReadOnlyDictionary<string, TalentTreeInstance>> Contents { get; } =
        new(new ReadOnlyDictionary<string, TalentTreeInstance>(new Dictionary<string, TalentTreeInstance>(StringComparer.Ordinal)));
    public IReadOnlyDictionary<string, TalentTreeInstance> Trees => Contents.Value;
}
