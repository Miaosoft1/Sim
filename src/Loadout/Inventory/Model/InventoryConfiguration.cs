using System.Collections.ObjectModel;

namespace Miaosoft.Sim.Loadout.Inventory.Model;

public sealed class InventoryConfiguration
{
    public InventoryConfiguration(int backpackCapacity, int carryCapacity, IReadOnlyDictionary<string, string> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (backpackCapacity < 0) throw new ArgumentOutOfRangeException(nameof(backpackCapacity));
        if (carryCapacity < 0) throw new ArgumentOutOfRangeException(nameof(carryCapacity));
        if (slots.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key != pair.Key.Trim() ||
            string.IsNullOrWhiteSpace(pair.Value) || pair.Value != pair.Value.Trim()))
            throw new ArgumentException("Equipment slots require normalized IDs and types.", nameof(slots));
        BackpackCapacity = backpackCapacity;
        CarryCapacity = carryCapacity;
        Slots = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(slots, StringComparer.Ordinal));
    }

    public int BackpackCapacity { get; }
    public int CarryCapacity { get; }
    public IReadOnlyDictionary<string, string> Slots { get; }
}
