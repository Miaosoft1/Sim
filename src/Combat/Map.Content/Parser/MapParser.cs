using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Content.Dto;

namespace Miaosoft.Sim.Combat.Map.Content.Parser
{
    internal sealed class MapParser
    {
        public MapDefinition Parse(MapDto dto)
        {
            ArgumentNullException.ThrowIfNull(dto);

            var dungeons = dto.Dungeons?.Select(b => new MapDungeon(
                id: b.Id,
                name: b.Name,
                position: new Vector2D(b.X, b.Y),
                size: new Vector2D(b.Width ?? 10, b.Height ?? 10),
                entrancePosition: new Vector2D(b.EntranceX ?? b.X, b.EntranceY ?? b.Y),
                interactionRadius: b.InteractionRadius ?? 5.0,
                description: b.Description,
                targetMapId: b.TargetMapId
            )).ToList();

            var rooms = dto.Rooms?.Select(r => new MapRoomDefinition(
                id: r.Id,
                name: r.Name,
                roomType: r.Type ?? "Custom",
                x: r.X,
                y: r.Y,
                width: r.Width,
                height: r.Height,
                isEntrance: r.IsEntrance,
                isExit: r.IsExit,
                description: r.Description
            )).ToList();

            var corridors = dto.Corridors?.Select(c => new MapCorridorDefinition(
                id: c.Id,
                fromRoomId: c.FromRoomId,
                toRoomId: c.ToRoomId,
                startX: c.StartX,
                startY: c.StartY,
                endX: c.EndX,
                endY: c.EndY,
                width: c.Width ?? 3.0,
                name: c.Name
            )).ToList();

            return new MapDefinition(
                id: dto.Id,
                name: dto.Name,
                width: dto.Width,
                height: dto.Height,
                type: dto.Type ?? "Overworld",
                boundaryType: dto.BoundaryType,
                dungeons: dungeons,
                rooms: rooms,
                corridors: corridors,
                tradeIsolated: dto.TradeIsolated,
                keepSourceVisible: dto.KeepSourceVisible,
                appearanceOverlay: dto.AppearanceOverlay,
                saturation: dto.Saturation,
                brightness: dto.Brightness,
                isMainPlane: dto.IsMainPlane ??
                    throw new InvalidOperationException($"Map '{dto.Id}' must declare isMainPlane.")
            );
        }

    }
}
