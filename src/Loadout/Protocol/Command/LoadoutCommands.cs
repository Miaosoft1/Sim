using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;

namespace Miaosoft.Sim.Loadout.Protocol.Command;

public abstract record LoadoutCommand : IEventMessage
{
    public Guid UnitId { get; init; }
}

public enum InventoryAction { Unspecified, Equip, Unequip, Discard }

public sealed record InventoryCommand : LoadoutCommand
{
    public Guid ItemInstanceId { get; init; }
    public long ExpectedRevision { get; init; }
    public InventoryAction Action { get; init; }
    public string? SlotId { get; init; }
}

public sealed record TalentAllocateCommand : LoadoutCommand
{
    public string TreeId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public long ExpectedRevision { get; init; }
    public long ExpectedAuthorityVersion { get; init; }
}

public sealed record ItemOffer(Guid ItemInstanceId, string? DestinationSlotId = null)
{
    public ItemOffer() : this(Guid.Empty) { }
}
public enum ExchangeAction { Unspecified, Offer, Confirm, Cancel }

public sealed record ExchangeCommand : LoadoutCommand
{
    public Guid ExchangeId { get; init; }
    public ExchangeAction Action { get; init; }
    public long ExpectedTermsRevision { get; init; }
    public IReadOnlyList<ItemOffer> Offers { get; init; } = [];
}

public sealed record DeathInspectCommand : LoadoutCommand
{
    public Guid AuthorizationId { get; init; }
}

public sealed record DeathClaimCommand : LoadoutCommand
{
    public Guid AuthorizationId { get; init; }
    public long ExpectedInspectionRevision { get; init; }
    public IReadOnlyList<ItemOffer> Items { get; init; } = [];
}

public static class LoadoutEventContracts
{
    public static EventCodecBuilder AddLoadout(this EventCodecBuilder builder) => builder
        .Register<InventoryCommand>("loadout.inventory", EventDirection.ClientToServer)
        .Register<TalentAllocateCommand>("loadout.talent.allocate", EventDirection.ClientToServer)
        .Register<ExchangeCommand>("loadout.exchange", EventDirection.ClientToServer)
        .Register<DeathInspectCommand>("loadout.death.inspect", EventDirection.ClientToServer)
        .Register<DeathClaimCommand>("loadout.death.claim", EventDirection.ClientToServer);
}
