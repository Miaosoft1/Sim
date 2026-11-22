using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

public sealed record DeathTradeAllocation(Unit Beneficiary, IReadOnlyList<Guid> ItemIds);

public interface IDeathTradeDistributionPolicy
{
    IEnumerable<DeathTradeAllocation> Allocate(DeathResolvedEvent death, IReadOnlyList<ItemInstance> items);
}
