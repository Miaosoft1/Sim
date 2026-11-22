using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.Policy;

internal static class EquipmentPreparation
{
    public static AbilitySourceChangePlan Compose(params AbilitySourceChangePlan[] plans)
    {
        if (plans.Any(plan => !plan.ShouldCommit))
            throw new InvalidOperationException("An equipment participant rejected an already validated inventory operation.");
        return new()
        {
            Materialize = plans.SelectMany(plan => plan.Materialize).ToArray(),
            Assignments = plans.SelectMany(plan => plan.Assignments).ToArray(),
            LevelChanges = plans.SelectMany(plan => plan.LevelChanges).ToArray(),
            RegistrationChanges = plans.SelectMany(plan => plan.RegistrationChanges).GroupBy(change => change.Target)
                .Select(group => new AtomicUnitAbilityChange(group.Key,
                    group.SelectMany(change => change.Remove).ToArray(), group.SelectMany(change => change.Add).ToArray())).ToArray(),
            Completions = plans.SelectMany(plan => plan.Completions).ToArray()
        };
    }

    public static AbilitySourceChangePlan Prepare(
        Event origin, Guid operationId, UnitInventory inventory,
        IReadOnlyList<ItemPlacement> before, IReadOnlyList<ItemPlacement> after, AtomicTransaction transaction) =>
        inventory.Equipment?.Prepare(origin, operationId, inventory, before, after, transaction) ?? new();
}
