using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Loadout.Inventory.Model;

public sealed class ItemDefinition
{
    public ItemDefinition(string id, string name, string? slotType, IEnumerable<AbilitySourceMember> abilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(abilities);
        if (id != id.Trim() || slotType != null && (string.IsNullOrWhiteSpace(slotType) || slotType != slotType.Trim()))
            throw new ArgumentException("Item IDs and slot types must be normalized.");
        AbilitySourceMember[] members = abilities.ToArray();
        if (members.Any(member => member == null || string.IsNullOrWhiteSpace(member.AbilityId) ||
            member.AbilityId != member.AbilityId.Trim() || member.Level < 1))
            throw new ArgumentException("Item abilities require normalized IDs and positive levels.", nameof(abilities));
        Id = id;
        Name = name;
        SlotType = slotType;
        Abilities = Array.AsReadOnly(members);
    }

    public string Id { get; }
    public string Name { get; }
    public string? SlotType { get; }
    public IReadOnlyList<AbilitySourceMember> Abilities { get; }
}
