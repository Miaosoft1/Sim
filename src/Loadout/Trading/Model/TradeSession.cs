using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Trading.Model;

public sealed record TradeItemOffer(Guid ItemInstanceId, string? DestinationSlotId = null);

public sealed class TradeSession
{
    internal sealed record State(
        long TermsRevision, TradeItemOffer[] FirstOffer, TradeItemOffer[] SecondOffer,
        long FirstInventoryRevision, long SecondInventoryRevision,
        bool FirstConfirmed, bool SecondConfirmed, bool Closed);
    internal AtomicCell<State> Runtime { get; }

    public TradeSession(UnitInventory first, UnitInventory second, Func<Unit, Unit, bool> canTrade)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(canTrade);
        if (ReferenceEquals(first.Owner, second.Owner))
            throw new ArgumentException("Trading requires two different inventory owners.");
        First = first;
        Second = second;
        CanTrade = canTrade;
        Runtime = new(new(0, [], [], first.Revision, second.Revision, false, false, false));
    }

    public Guid InstanceId { get; } = Guid.NewGuid();
    public UnitInventory First { get; }
    public UnitInventory Second { get; }
    internal Func<Unit, Unit, bool> CanTrade { get; }
    public long TermsRevision => Runtime.Value.TermsRevision;
    public bool IsClosed => Runtime.Value.Closed;
    public bool FirstConfirmed => Runtime.Value.FirstConfirmed;
    public bool SecondConfirmed => Runtime.Value.SecondConfirmed;
    public bool InventoriesCurrent => Runtime.Value.FirstInventoryRevision == First.Revision &&
        Runtime.Value.SecondInventoryRevision == Second.Revision;
    public IReadOnlyList<TradeItemOffer> FirstOffer => Array.AsReadOnly(Runtime.Value.FirstOffer);
    public IReadOnlyList<TradeItemOffer> SecondOffer => Array.AsReadOnly(Runtime.Value.SecondOffer);
}
