using System.Collections.Frozen;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Server;

public sealed class LoadoutSessionFeatures
{
    public LoadoutSessionFeatures(
        Context context, IEnumerable<LoadoutParticipant> units,
        IEnumerable<TradeSession>? exchanges = null, IEnumerable<DeathTradeDirectory>? deaths = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(units);
        Context = context;
        Units = units.ToFrozenDictionary(unit => unit.Unit.Id);
        Exchanges = (exchanges ?? []).ToFrozenDictionary(exchange => exchange.InstanceId);
        Deaths = Array.AsReadOnly((deaths ?? []).ToArray());
        if (Units.Values.Any(feature =>
            feature.Unit.EventReceiver.Context != context ||
            !context.ContainsReceiver(feature.Unit.EventReceiver) ||
            feature.Inventory != null && !ReferenceEquals(feature.Inventory.Owner, feature.Unit) ||
            feature.Talents != null && !ReferenceEquals(feature.Talents.Owner, feature.Unit)))
            throw new ArgumentException("Loadout features must belong to initialized units in this context.", nameof(units));
        if (Deaths.Select(directory => directory.Inventory.Owner.Id).Distinct().Count() != Deaths.Count ||
            Exchanges.Values.Any(exchange =>
                !OwnsInventory(exchange.First) || !OwnsInventory(exchange.Second)) ||
            Deaths.Any(directory => !OwnsInventory(directory.Inventory)))
            throw new ArgumentException("Exchange and death directories must use this snapshot's unit inventories.");
    }

    public Context Context { get; }
    public IReadOnlyDictionary<Guid, LoadoutParticipant> Units { get; }
    public IReadOnlyDictionary<Guid, TradeSession> Exchanges { get; }
    public IReadOnlyList<DeathTradeDirectory> Deaths { get; }

    private bool OwnsInventory(UnitInventory inventory) =>
        Units.TryGetValue(inventory.Owner.Id, out LoadoutParticipant? feature) &&
        ReferenceEquals(feature.Inventory, inventory);
}
