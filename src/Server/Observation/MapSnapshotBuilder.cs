using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Hex;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Server.Observation;

public static class MapSnapshotBuilder
{
    public static MapSnapshot Capture(MapRuntime map, Func<MapObject, bool> observePortal)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(observePortal);
        MapDefinition? definition = map.Definition;
        return new()
        {
            InstanceId = map.InstanceId, DefinitionId = map.Id, Name = map.Name,
            Width = map.Width, Height = map.Height, IsTorus = map.Space.IsWrapped,
            IsInterior = definition?.IsInterior == true, BoundaryType = definition?.BoundaryType ?? "",
            Rooms = definition == null ? [] : Array.AsReadOnly(definition.Rooms.Select(room => new MapRoomSnapshot
            {
                Id = room.Id, Name = room.Name, Kind = room.RoomType, Position = new(room.X, room.Y),
                Size = new(room.Width, room.Height), IsEntrance = room.IsEntrance, IsExit = room.IsExit
            }).ToArray()),
            Corridors = definition == null ? [] : Array.AsReadOnly(definition.Corridors.Select(corridor => new MapCorridorSnapshot
            {
                Id = corridor.Id, FromRoomId = corridor.FromRoomId, ToRoomId = corridor.ToRoomId,
                Start = new(corridor.StartX, corridor.StartY), End = new(corridor.EndX, corridor.EndY), Width = corridor.Width
            }).ToArray()),
            HexGrid = map.UnitGrid is HexBoardMap board ? CaptureGrid(board.Layout) : null,
            Portals = Array.AsReadOnly(map.Rooms.Where(room => room.Dungeon.IsActive &&
                    map.GetDungeonMap(room.Dungeon.Id) is { IsClosing: false } && observePortal(room.Dungeon))
                .OrderBy(room => room.Dungeon.Id, StringComparer.Ordinal).Select(room => new MapPortalSnapshot
                {
                    Id = room.Dungeon.Id, DestinationInstanceId = map.GetDungeonMap(room.Dungeon.Id)!.InstanceId,
                    Position = new(room.Dungeon.Position.X, room.Dungeon.Position.Y),
                    Size = new(room.Dungeon.Size.X, room.Dungeon.Size.Y)
                }).ToArray())
        };
    }

    private static HexGridSnapshot CaptureGrid(HexBoardLayout layout) => new()
    {
        CellSize = layout.CellSize,
        Orientation = layout.Orientation switch
        {
            HexOrientation.PointyTop => HexGridOrientation.PointyTop,
            HexOrientation.FlatTop => HexGridOrientation.FlatTop,
            _ => throw new InvalidOperationException("The board orientation is unsupported.")
        },
        Origin = new(layout.Origin.X, layout.Origin.Y),
        Cells = Array.AsReadOnly(layout.Definition.PlayableCells.OrderBy(cell => cell.Q).ThenBy(cell => cell.R)
            .Select(cell =>
            {
                var center = layout.ToWorld(cell);
                return new HexCellSnapshot { Q = cell.Q, R = cell.R, Center = new(center.X, center.Y) };
            }).ToArray())
    };
}
