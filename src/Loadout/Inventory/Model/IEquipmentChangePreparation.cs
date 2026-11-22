using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Loadout.Inventory.Model;

public interface IEquipmentChangePreparation
{
    /// <summary>Stage validated equipment effects only; do not construct handlers, commit, publish, or return a rejected plan.</summary>
    AbilitySourceChangePlan Prepare(
        Event origin, Guid operationId, UnitInventory inventory,
        IReadOnlyList<ItemPlacement> before, IReadOnlyList<ItemPlacement> after, AtomicTransaction transaction);
}
