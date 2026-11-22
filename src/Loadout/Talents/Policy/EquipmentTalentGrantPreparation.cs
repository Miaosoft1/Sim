using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Talents.Policy;

public sealed class EquipmentTalentGrantPreparation : IEquipmentChangePreparation
{
    private readonly UnitTalents talents;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TalentTreeDefinition>> bindings;

    public EquipmentTalentGrantPreparation(
        UnitTalents talents, IReadOnlyDictionary<string, IReadOnlyList<TalentTreeDefinition>> bindings)
    {
        ArgumentNullException.ThrowIfNull(talents);
        ArgumentNullException.ThrowIfNull(bindings);
        var definitions = new Dictionary<string, IReadOnlyList<TalentTreeDefinition>>(StringComparer.Ordinal);
        foreach (var (itemId, trees) in bindings)
        {
            if (string.IsNullOrWhiteSpace(itemId) || itemId != itemId.Trim() || trees == null ||
                trees.Any(tree => tree == null) ||
                trees.Select(tree => tree.Id).Distinct(StringComparer.Ordinal).Count() != trees.Count)
                throw new ArgumentException("Equipment tree bindings require normalized item IDs and distinct tree definitions.", nameof(bindings));
            definitions.Add(itemId, Array.AsReadOnly(trees.ToArray()));
        }
        this.talents = talents;
        this.bindings = new ReadOnlyDictionary<string, IReadOnlyList<TalentTreeDefinition>>(definitions);
    }

    public AbilitySourceChangePlan Prepare(
        Event origin, Guid operationId, UnitInventory inventory,
        IReadOnlyList<ItemPlacement> before, IReadOnlyList<ItemPlacement> after, AtomicTransaction transaction)
    {
        if (!ReferenceEquals(inventory.Owner, talents.Owner))
            throw new InvalidOperationException("An equipment bridge must use its own owner's talent directory.");
        TalentAccess.Validate(origin.Context, talents, inventory.Owner, inventory.Owner.EventReceiver);
        var wasEquipped = before.Where(placement => placement.SlotId != null).Select(placement => placement.Item).ToHashSet();
        var equipped = after.Where(placement => placement.SlotId != null).Select(placement => placement.Item).ToHashSet();
        var changes = new Dictionary<string, (TalentTreeDefinition Definition, List<(IAbilitySource Source, bool Authorize)> Grants)>(
            StringComparer.Ordinal);
        void Change(ItemInstance item, bool authorize)
        {
            if (!bindings.TryGetValue(item.Definition.Id, out var trees)) return;
            foreach (TalentTreeDefinition definition in trees)
            {
                if (!changes.TryGetValue(definition.Id, out var batch))
                {
                    batch = (definition, []);
                    changes.Add(definition.Id, batch);
                }
                if (!ReferenceEquals(batch.Definition, definition))
                    throw new InvalidOperationException("An equipment bridge cannot mix catalogs for one tree ID.");
                batch.Grants.Add((item.Source, authorize));
            }
        }
        foreach (ItemInstance item in wasEquipped.Except(equipped)) Change(item, false);
        foreach (ItemInstance item in equipped.Except(wasEquipped)) Change(item, true);
        return EquipmentPreparation.Compose(changes.Values.Select(batch => TalentAuthorizationPreparation.Stage(
            origin, operationId, talents, batch.Definition, batch.Grants, transaction)).ToArray());
    }
}
