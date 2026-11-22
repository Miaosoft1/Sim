using System.Collections.ObjectModel;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Trading.Policy;

namespace Miaosoft.Sim.Loadout.Trading.Model;

public sealed class DeathTradeDirectory
{
    internal sealed record State(Guid? DeathId, IReadOnlyDictionary<Guid, DeathTradeAuthorization> Authorizations, bool Closed);
    internal AtomicCell<State> Runtime { get; } = new(new(null,
        new ReadOnlyDictionary<Guid, DeathTradeAuthorization>(new Dictionary<Guid, DeathTradeAuthorization>()), false));

    public DeathTradeDirectory(
        UnitInventory inventory, IDeathTradeDistributionPolicy distribution,
        Func<DeathTradeAuthorization, bool> canInteract)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(distribution);
        ArgumentNullException.ThrowIfNull(canInteract);
        Inventory = inventory;
        Distribution = distribution;
        CanInteract = canInteract;
    }

    public UnitInventory Inventory { get; }
    internal IDeathTradeDistributionPolicy Distribution { get; }
    internal Func<DeathTradeAuthorization, bool> CanInteract { get; }
    public Guid? DeathId => Runtime.Value.DeathId;
    public bool IsClosed => Runtime.Value.Closed;
    public IReadOnlyDictionary<Guid, DeathTradeAuthorization> Authorizations => Runtime.Value.Authorizations;
}
