namespace Miaosoft.Sim.Protocol.Model;

public sealed record MapSnapshot
{
    public required Guid InstanceId { get; init; }
    public required string DefinitionId { get; init; }
    public required string Name { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public bool IsTorus { get; init; }
    public bool IsInterior { get; init; }
    public string BoundaryType { get; init; } = "";
    public IReadOnlyList<MapRoomSnapshot> Rooms { get; init; } = [];
    public IReadOnlyList<MapCorridorSnapshot> Corridors { get; init; } = [];
    public HexGridSnapshot? HexGrid { get; init; }
    public IReadOnlyList<MapPortalSnapshot> Portals { get; init; } = [];
}

public sealed record MapPortalSnapshot
{
    public required string Id { get; init; }
    public required Guid DestinationInstanceId { get; init; }
    public required WorldPosition Position { get; init; }
    public required WorldPosition Size { get; init; }
}

public enum HexGridOrientation { PointyTop, FlatTop }

public sealed record HexGridSnapshot
{
    public required double CellSize { get; init; }
    public required HexGridOrientation Orientation { get; init; }
    public required WorldPosition Origin { get; init; }
    public required IReadOnlyList<HexCellSnapshot> Cells { get; init; }
}

public sealed record HexCellSnapshot
{
    public required int Q { get; init; }
    public required int R { get; init; }
    public required WorldPosition Center { get; init; }
}

public sealed record MapRoomSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required WorldPosition Position { get; init; }
    public required WorldPosition Size { get; init; }
    public bool IsEntrance { get; init; }
    public bool IsExit { get; init; }
}

public sealed record MapCorridorSnapshot
{
    public required string Id { get; init; }
    public required string FromRoomId { get; init; }
    public required string ToRoomId { get; init; }
    public required WorldPosition Start { get; init; }
    public required WorldPosition End { get; init; }
    public required double Width { get; init; }
}
