using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.EventData;

public enum InventoryOperation
{
    Acquire,
    Equip,
    Unequip,
    Discard
}

public enum InventoryChangeFailure
{
    None,
    StaleRevision,
    NotOwned,
    AlreadyOwnedOrDestroyed,
    CapacityExceeded,
    InvalidSlot
}

public sealed record InventoryChangeRequestEvent : Event
{
    public required Unit Owner { get; init; }
    public required ItemInstance Item { get; init; }
    public required InventoryOperation Operation { get; init; }
    public string? SlotId { get; init; }
    public long? ExpectedRevision { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public sealed record InventoryChangeResultEvent : Event
{
    public required Guid OperationId { get; init; }
    public required Unit Owner { get; init; }
    public required InventoryOperation Operation { get; init; }
    public required Guid ItemInstanceId { get; init; }
    public required InventoryChangeFailure Failure { get; init; }
    public bool Success => Failure == InventoryChangeFailure.None;
}
