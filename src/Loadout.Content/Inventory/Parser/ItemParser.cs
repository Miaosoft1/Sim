using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Loadout.Content.Inventory.Dto;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Content.Inventory.Parser;

internal sealed class ItemParser
{
    public ItemDefinition Compile(ItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        string id = dto.Id ?? throw new InvalidDataException("An item requires an ID.");
        string name = dto.Name ?? throw new InvalidDataException($"Item '{id}' requires a name.");
        List<ItemAbilityDto> abilities = dto.Abilities
            ?? throw new InvalidDataException($"Item '{id}' abilities must be a list.");
        return new(id, name, dto.SlotType, abilities.Select(member =>
            member?.Id is { } abilityId
                ? new AbilitySourceMember(abilityId, member.Level)
                : throw new InvalidDataException($"Item '{id}' requires an ID for each ability.")));
    }
}
