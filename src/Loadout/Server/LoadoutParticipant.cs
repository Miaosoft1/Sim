using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Server;

public sealed record LoadoutParticipant(Unit Unit, UnitInventory? Inventory = null, UnitTalents? Talents = null);
