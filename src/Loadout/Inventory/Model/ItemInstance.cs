using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Loadout.Inventory.Model;

public sealed class ItemInstance
{
    internal sealed record State(Context? Context, Unit? Owner, bool Destroyed);
    internal AtomicCell<State> Runtime { get; } = new(new(null, null, false));

    internal ItemInstance(ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        Source = new AbilitySourceInstance("Equipment", definition.Abilities);
    }

    public Guid InstanceId { get; } = Guid.NewGuid();
    public ItemDefinition Definition { get; }
    public AbilitySourceInstance Source { get; }
    public long Revision => Runtime.Version;
    public Guid? OwnerId => Runtime.Value.Owner?.Id;
    public bool IsDestroyed => Runtime.Value.Destroyed;
}
