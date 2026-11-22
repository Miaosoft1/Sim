using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.EventData;

public enum DeathTradeOperation { Authorize, Inspect, Claim, Cleanup }
public enum DeathTradeFailure
{
    None, AlreadyAuthorized, Closed, NotAuthorized, CorpseUnavailable, PolicyRejected,
    NotInspected, StaleInspection, InventoryChanged, InvalidSelection, InvalidLayout
}

public sealed record DeathTradeAuthorizedEvent : Event
{
    public required DeathTradeAuthorization Authorization { get; init; }
}

public abstract record DeathTradeRequestEvent : Event
{
    public required Unit Actor { get; init; }
    public required DeathTradeAuthorization Authorization { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public sealed record DeathTradeInspectRequestEvent : DeathTradeRequestEvent;

public sealed record DeathTradeClaimRequestEvent : DeathTradeRequestEvent
{
    public required long ExpectedInspectionRevision { get; init; }
    public required IReadOnlyList<TradeItemOffer> Items { get; init; }
}

public sealed record DeathTradeCleanupRequestEvent : Event
{
    public required DeathTradeDirectory Directory { get; init; }
    public required Guid ExpectedDeathId { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public sealed record DeathTradeItemView(Guid InstanceId, string DefinitionId, string Name, string? SlotId);

public sealed record DeathTradeResultEvent : Event
{
    public required Guid OperationId { get; init; }
    public required DeathTradeOperation Operation { get; init; }
    public required DeathTradeFailure Failure { get; init; }
    public Guid? AuthorizationId { get; init; }
    public required Guid DeathId { get; init; }
    public long InspectionRevision { get; init; }
    public IReadOnlyList<DeathTradeItemView> Items { get; init; } = [];
    public bool Success => Failure == DeathTradeFailure.None;
}
