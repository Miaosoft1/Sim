using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.EventData;

public sealed record ItemCreateRequestEvent : Event
{
    public required Unit Coordinator { get; init; }
    public required ItemDefinition Definition { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public sealed record ItemCreatedEvent : Event
{
    public required Guid OperationId { get; init; }
    public required ItemInstance Item { get; init; }
}
