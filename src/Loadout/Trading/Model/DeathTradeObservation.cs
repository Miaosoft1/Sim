using Miaosoft.Sim.Loadout.Trading.EventData;

namespace Miaosoft.Sim.Loadout.Trading.Model;

public sealed record DeathTradeObservation(
    DeathTradeFailure Access, long InspectionRevision, IReadOnlyList<DeathTradeItemView> Items);
