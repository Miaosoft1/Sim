using Miaosoft.Sim.Loadout.Protocol;
using Miaosoft.Sim.Loadout.Protocol.Command;
using Miaosoft.Sim.Loadout.Protocol.Model;
using Miaosoft.Sim.Loadout.Trading.Model;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Loadout.Server;

public sealed class LoadoutObservationModule : ISessionObservationModule
{
    private readonly Func<LoadoutSessionFeatures> features;
    private readonly LoadoutSnapshotCodec codec = new();

    internal LoadoutObservationModule(Func<LoadoutSessionFeatures> features) => this.features = features;

    public string Id => LoadoutSnapshotCodec.ModuleId;
    public int Version => LoadoutSnapshotCodec.ModuleVersion;
    public bool RequiresControlledUnit => true;

    public ModuleProjection Capture(Guid? unitId, long stateVersion, bool full, string? previousPublishedState)
    {
        LoadoutSessionFeatures current = features();
        if (unitId == null || !current.Units.TryGetValue(unitId.Value, out LoadoutParticipant? participant))
            return new(ModuleProjectionKind.Removed);
        bool inventory = LoadoutFeatureAccess.Inventory(participant.Inventory);
        bool talents = LoadoutFeatureAccess.Talents(participant.Talents);
        var snapshot = new LoadoutSnapshot
        {
            UnitId = participant.Unit.Id,
            Inventory = !inventory ? null : new()
            {
                Revision = participant.Inventory!.Revision,
                Items = Array.AsReadOnly(participant.Inventory.Items.Select(placement => new ItemSnapshot
                {
                    InstanceId = placement.Item.InstanceId, DefinitionId = placement.Item.Definition.Id,
                    Name = placement.Item.Definition.Name, SlotId = placement.SlotId,
                    AbilityInstanceIds = Array.AsReadOnly(placement.Item.Source.Abilities
                        .Select(ability => ability.RuntimeInstanceId).ToArray())
                }).ToArray())
            },
            Talents = !talents ? [] : Array.AsReadOnly(participant.Talents!.Trees.Values.Select(tree => new TalentSnapshot
            {
                TreeId = tree.Definition.Id, InstanceId = tree.InstanceId, Revision = tree.Revision,
                AuthorityVersion = tree.AuthorityVersion, IsAuthorized = tree.IsAuthorized,
                AvailablePoints = tree.AvailablePoints,
                Ranks = Array.AsReadOnly(tree.Ranks.Select(rank => new TalentRankSnapshot(rank.Key, rank.Value)).ToArray()),
                Growth = Array.AsReadOnly(tree.Growth.Select(progress =>
                    new TalentProgressSnapshot(progress.Key, progress.Value.PendingProgress, progress.Value.Rewards)).ToArray())
            }).ToArray()),
            Exchanges = !inventory ? [] : Array.AsReadOnly(current.Exchanges.Values
                .Where(exchange => ReferenceEquals(exchange.First, participant.Inventory) || ReferenceEquals(exchange.Second, participant.Inventory))
                .Where(exchange => LoadoutFeatureAccess.Trading(exchange.First) && LoadoutFeatureAccess.Trading(exchange.Second))
                .Select(exchange => Capture(exchange, participant)).ToArray()),
            DeathInspections = !inventory ? [] : Array.AsReadOnly(current.Deaths.Where(LoadoutFeatureAccess.Death)
                .Select(directory => directory.Authorizations.GetValueOrDefault(participant.Unit.Id))
                .Where(authorization => authorization != null).Select(authorization => Capture(authorization!)).ToArray())
        };
        string payload = codec.Write(snapshot);
        return !full && payload == previousPublishedState ? new(ModuleProjectionKind.Unchanged) :
            new(ModuleProjectionKind.Full, payload, payload);
    }

    private static ExchangeSnapshot Capture(TradeSession exchange, LoadoutParticipant participant)
    {
        bool first = ReferenceEquals(exchange.First, participant.Inventory);
        return new()
        {
            InstanceId = exchange.InstanceId, OtherUnitId = (first ? exchange.Second : exchange.First).Owner.Id,
            TermsRevision = exchange.TermsRevision, IsClosed = exchange.IsClosed,
            SelfConfirmed = first ? exchange.FirstConfirmed : exchange.SecondConfirmed,
            OtherConfirmed = first ? exchange.SecondConfirmed : exchange.FirstConfirmed,
            InventoriesCurrent = exchange.InventoriesCurrent,
            SelfOffer = Array.AsReadOnly((first ? exchange.FirstOffer : exchange.SecondOffer)
                .Select(offer => new ItemOffer(offer.ItemInstanceId, offer.DestinationSlotId)).ToArray()),
            OtherOffer = Array.AsReadOnly((first ? exchange.SecondOffer : exchange.FirstOffer)
                .Select(offer => new ItemOffer(offer.ItemInstanceId, offer.DestinationSlotId)).ToArray()),
            OtherItems = exchange.IsClosed ? [] : Array.AsReadOnly((first ? exchange.Second : exchange.First).Items
                .Where(placement => (first ? exchange.SecondOffer : exchange.FirstOffer)
                    .Any(offer => offer.ItemInstanceId == placement.Item.InstanceId))
                .Select(placement => new ItemSnapshot
                {
                    InstanceId = placement.Item.InstanceId, DefinitionId = placement.Item.Definition.Id,
                    Name = placement.Item.Definition.Name
                }).ToArray())
        };
    }

    private static DeathInspectionSnapshot Capture(DeathTradeAuthorization authorization)
    {
        DeathTradeObservation observation = authorization.Observe();
        return new()
        {
            AuthorizationId = authorization.InstanceId, OwnerUnitId = authorization.Owner.Id,
            DeathId = authorization.DeathId, Revision = observation.InspectionRevision, Access = observation.Access.ToString(),
            Items = Array.AsReadOnly(observation.Items.Select(item => new ItemSnapshot
            {
                InstanceId = item.InstanceId, DefinitionId = item.DefinitionId, Name = item.Name, SlotId = item.SlotId
            }).ToArray())
        };
    }
}
