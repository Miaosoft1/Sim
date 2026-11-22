using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Combat.Map.Content.Dto;

namespace Miaosoft.Sim.Combat.Map.Content.Validation;

internal static class MapContentValidator
{
    public static void Validate(IReadOnlyDictionary<string, MapDto> maps, ContentValidationContext context)
    {
        IReadOnlySet<string> mapIds = maps.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (MapDto map in maps.Values)
        {
            foreach (MapDungeonDto dungeon in map.Dungeons ?? [])
            {
                if (dungeon == null)
                    throw new InvalidDataException($"{context.Source(map)}: map '{map.Id}' contains a null dungeon.");
                if (dungeon.TargetMapId != null)
                    context.RequireReference(map, $"map '{map.Id}'.dungeon '{dungeon.Id}'.targetMapId", dungeon.TargetMapId, mapIds);
            }
            var rooms = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapRoomDto room in map.Rooms ?? [])
                if (room == null || string.IsNullOrWhiteSpace(room.Id) || !rooms.Add(room.Id))
                    throw new InvalidDataException($"{context.Source(map)}: map '{map.Id}' has a null, duplicate or empty room ID '{room?.Id}'.");
            foreach (MapCorridorDto corridor in map.Corridors ?? [])
            {
                if (corridor == null)
                    throw new InvalidDataException($"{context.Source(map)}: map '{map.Id}' contains a null corridor.");
                context.RequireReference(map, $"map '{map.Id}'.corridor '{corridor.Id}'.fromRoomId", corridor.FromRoomId, rooms);
                context.RequireReference(map, $"map '{map.Id}'.corridor '{corridor.Id}'.toRoomId", corridor.ToRoomId, rooms);
            }
        }
    }
}
