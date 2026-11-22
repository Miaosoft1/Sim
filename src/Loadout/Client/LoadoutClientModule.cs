using Miaosoft.Sim.Client;
using Miaosoft.Sim.Client.Observation;
using Miaosoft.Sim.Loadout.Protocol;
using Miaosoft.Sim.Loadout.Protocol.Command;
using Miaosoft.Sim.Loadout.Protocol.Model;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Loadout.Client;

public sealed class LoadoutClientModule : IClientObservationModule
{
    private readonly LoadoutSnapshotCodec codec = new();
    public string Id => LoadoutSnapshotCodec.ModuleId;
    public int Version => LoadoutSnapshotCodec.ModuleVersion;

    public object ReadFull(string payload, SessionBinding binding)
    {
        LoadoutSnapshot snapshot = codec.Read(payload);
        if (snapshot.UnitId == Guid.Empty || snapshot.UnitId != binding.UnitId || snapshot.Inventory is { Revision: < 0 } ||
            snapshot.Inventory != null && !ValidItems(snapshot.Inventory.Items) ||
            snapshot.Talents == null || snapshot.Exchanges == null || snapshot.DeathInspections == null ||
            snapshot.Talents.Any(tree => tree == null || string.IsNullOrWhiteSpace(tree.TreeId) ||
                tree.InstanceId == Guid.Empty || tree.Revision < 0 || tree.AuthorityVersion < 1 || tree.AvailablePoints < 0 ||
                tree.Ranks == null || tree.Growth == null ||
                tree.Ranks.Any(rank => rank == null || string.IsNullOrWhiteSpace(rank.NodeId) || rank.Rank < 1) ||
                tree.Ranks.Select(rank => rank.NodeId).Distinct(StringComparer.Ordinal).Count() != tree.Ranks.Count ||
                tree.Growth.Any(progress => progress == null || string.IsNullOrWhiteSpace(progress.RuleId) ||
                    progress.PendingProgress < 0 || progress.Rewards < 0) ||
                tree.Growth.Select(progress => progress.RuleId).Distinct(StringComparer.Ordinal).Count() != tree.Growth.Count) ||
            snapshot.Talents.Select(tree => tree.TreeId).Distinct(StringComparer.Ordinal).Count() != snapshot.Talents.Count ||
            snapshot.Talents.Select(tree => tree.InstanceId).Distinct().Count() != snapshot.Talents.Count ||
            snapshot.Exchanges.Any(exchange => exchange == null || exchange.InstanceId == Guid.Empty ||
                exchange.OtherUnitId == Guid.Empty || exchange.OtherUnitId == snapshot.UnitId || exchange.TermsRevision < 0 ||
                !ValidOffers(exchange.SelfOffer) || !ValidOffers(exchange.OtherOffer) || !ValidItems(exchange.OtherItems) ||
                exchange.OtherItems.Any(item => exchange.OtherOffer.All(offer => offer.ItemInstanceId != item.InstanceId))) ||
            snapshot.Exchanges.Select(exchange => exchange.InstanceId).Distinct().Count() != snapshot.Exchanges.Count ||
            snapshot.DeathInspections.Any(death => death == null || death.AuthorizationId == Guid.Empty ||
                death.OwnerUnitId == Guid.Empty || death.DeathId == Guid.Empty || death.Revision < 0 ||
                string.IsNullOrWhiteSpace(death.Access) || !ValidItems(death.Items) ||
                death.Access != "None" && death.Items.Count != 0) ||
            snapshot.DeathInspections.Select(death => death.AuthorizationId).Distinct().Count() != snapshot.DeathInspections.Count)
            throw new InvalidDataException("The Loadout snapshot contains invalid identities, versions or collections.");
        return snapshot with
        {
            Inventory = snapshot.Inventory is not { } inventory ? null : inventory with { Items = Freeze(inventory.Items) },
            Talents = Array.AsReadOnly(snapshot.Talents.Select(tree => tree with
            {
                Ranks = Array.AsReadOnly(tree.Ranks.ToArray()), Growth = Array.AsReadOnly(tree.Growth.ToArray())
            }).ToArray()),
            Exchanges = Array.AsReadOnly(snapshot.Exchanges.Select(exchange => exchange with
            {
                SelfOffer = Array.AsReadOnly(exchange.SelfOffer.ToArray()),
                OtherOffer = Array.AsReadOnly(exchange.OtherOffer.ToArray()), OtherItems = Freeze(exchange.OtherItems)
            }).ToArray()),
            DeathInspections = Array.AsReadOnly(snapshot.DeathInspections.Select(death =>
                death with { Items = Freeze(death.Items) }).ToArray())
        };
    }

    public object ApplyDelta(object previous, string payload, SessionBinding binding) =>
        throw new InvalidDataException("Loadout observation version 1 only accepts complete replacements or removal.");

    private static bool ValidOffers(IReadOnlyList<ItemOffer>? offers) => offers != null &&
        offers.All(offer => offer != null && offer.ItemInstanceId != Guid.Empty &&
            (offer.DestinationSlotId == null || !string.IsNullOrWhiteSpace(offer.DestinationSlotId))) &&
        offers.Select(offer => offer.ItemInstanceId).Distinct().Count() == offers.Count;

    private static bool ValidItems(IReadOnlyList<ItemSnapshot>? items) => items != null &&
        items.All(item => item != null && item.InstanceId != Guid.Empty && !string.IsNullOrWhiteSpace(item.DefinitionId) &&
            item.Name != null && (item.SlotId == null || !string.IsNullOrWhiteSpace(item.SlotId)) &&
            item.AbilityInstanceIds != null && item.AbilityInstanceIds.All(id => id != Guid.Empty) &&
            item.AbilityInstanceIds.Distinct().Count() == item.AbilityInstanceIds.Count) &&
        items.Select(item => item.InstanceId).Distinct().Count() == items.Count;

    private static IReadOnlyList<ItemSnapshot> Freeze(IReadOnlyList<ItemSnapshot> items) =>
        Array.AsReadOnly(items.Select(item => item with
        {
            AbilityInstanceIds = Array.AsReadOnly(item.AbilityInstanceIds.ToArray())
        }).ToArray());
}

public static class LoadoutClientExtensions
{
    public static LoadoutSnapshot? GetLoadout(this ClientObservationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Modules.TryGetValue(LoadoutSnapshotCodec.ModuleId, out object? value)) return null;
        if (value is not LoadoutSnapshot snapshot || snapshot.UnitId != state.Binding.UnitId)
            throw new InvalidDataException("The Loadout module belongs to another controlled unit.");
        return snapshot;
    }
}
