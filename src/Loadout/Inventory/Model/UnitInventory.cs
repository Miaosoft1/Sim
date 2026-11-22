using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Loadout.Inventory.Model;

public sealed record ItemPlacement(ItemInstance Item, string? SlotId);

public sealed class UnitInventory
{
    internal AtomicCell<ItemPlacement[]> Contents { get; } = new([]);

    public UnitInventory(Unit owner, InventoryConfiguration configuration, IEquipmentChangePreparation? equipment = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(configuration);
        Owner = owner;
        Configuration = configuration;
        Equipment = equipment;
    }

    public Unit Owner { get; }
    public InventoryConfiguration Configuration { get; }
    public IEquipmentChangePreparation? Equipment { get; }
    public long Revision => Contents.Version;
    public IReadOnlyList<ItemPlacement> Items => Array.AsReadOnly(Contents.Value);
    public int CarryCount => Contents.Value.Length;
    public int BackpackCount => Contents.Value.Count(placement => placement.SlotId == null);
}
