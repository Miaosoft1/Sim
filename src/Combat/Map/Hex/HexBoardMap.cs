using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Hex;

public sealed class HexBoardMap : IMapUnitGrid
{
    private readonly HexBoardRuntime occupancy;
    private readonly double speedUnitsPerCell;

    public HexBoardMap(HexBoardDefinition definition, double cellSize, HexOrientation orientation,
        string id, string name, double speedUnitsPerCell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!double.IsFinite(speedUnitsPerCell) || speedUnitsPerCell <= 0)
            throw new ArgumentOutOfRangeException(nameof(speedUnitsPerCell));
        this.speedUnitsPerCell = speedUnitsPerCell;
        Layout = new HexBoardLayout(definition, cellSize, orientation);
        occupancy = new HexBoardRuntime(definition);
        Map = new MapRuntime(Layout.Width, Layout.Height, id, name, isMainPlane: false, targetingMetric: Layout);
        Map.ConfigureUnitGrid(this);
    }

    public HexBoardLayout Layout { get; }
    public IReadOnlyDictionary<HexCoordinate, Guid> Occupants => occupancy.Occupants;
    public IReadOnlyDictionary<Guid, HexCoordinate> Positions => occupancy.Positions;
    public IReadOnlyDictionary<HexCoordinate, Guid> Reservations => occupancy.Reservations;
    public MapRuntime Map { get; }
    public bool HasReservation(Guid unitId) => occupancy.HasReservation(unitId);
    public double ToCellsPerSecond(double moveSpeed) => moveSpeed / speedUnitsPerCell;

    public bool TryPlace(MapObject body, HexCoordinate cell)
    {
        Guid unitId = RequireUnit(body);
        if (body.MapRuntime != null || body.Manager?.ControlledObject != body || Map.IsClosing)
            return false;
        if (!occupancy.TryPlace(unitId, cell)) return false;

        Vector2D previous = body.Position;
        try
        {
            body.SetUnplacedPosition(Layout.ToWorld(cell));
            Map.AddObject(body);
            return true;
        }
        catch
        {
            if (!ReferenceEquals(body.MapRuntime, Map))
            {
                occupancy.Remove(unitId);
                body.SetUnplacedPosition(previous);
            }
            throw;
        }
    }

    public bool TryTransferTo(HexBoardMap destination, MapObject body, HexCoordinate cell, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Guid unitId = RequireUnit(body);
        if (!ReferenceEquals(body.MapRuntime, Map) || body.Manager?.ControlledObject != body ||
            Map.Directory == null || !ReferenceEquals(Map.Directory, destination.Map.Directory) || destination.Map.IsClosing ||
            !body.Manager.CanSwitchParallelPlane(destination.Map, destination.Layout.ToWorld(cell), now))
            return false;
        if (!TryCurrentCell(body, unitId, out HexCoordinate sourceCell)) return false;
        HexCoordinate? reservedCell = occupancy.Reservations
            .Where(item => item.Value == unitId)
            .Select(item => (HexCoordinate?)item.Key)
            .SingleOrDefault();
        if (!occupancy.TryTransferTo(destination.occupancy, unitId, cell)) return false;
        try
        {
            body.Manager.SwitchGridParallelPlane(destination.Map, destination.Layout.ToWorld(cell), now);
            return true;
        }
        catch (Exception transferError)
        {
            if (ReferenceEquals(body.Manager.ControlledObject?.MapRuntime, destination.Map))
                throw new InvalidOperationException("The board transfer committed, but its change notification failed.",
                    transferError);
            if (!destination.occupancy.TryTransferTo(occupancy, unitId, sourceCell))
                throw new AggregateException("The board transfer failed and its occupancy could not be restored.",
                    transferError);
            if (reservedCell.HasValue && !occupancy.TryReserveMove(unitId, reservedCell.Value))
                throw new AggregateException("The board transfer failed and its reservation could not be restored.",
                    transferError);
            throw;
        }
    }

    public bool TryReposition(MapObject body, HexCoordinate destination)
    {
        Guid unitId = RequireUnit(body);
        if (Map.IsClosing || !body.IsActive || body.Movement is { EndReason: null } ||
            !TryCurrentCell(body, unitId, out HexCoordinate origin) ||
            !occupancy.TryReposition(unitId, destination))
            return false;
        if (origin == destination) return true;

        Vector2D targetPosition = Layout.ToWorld(destination);
        try
        {
            body.Position = targetPosition;
            return true;
        }
        catch (Exception error)
        {
            if (body.Position == targetPosition)
                throw new InvalidOperationException("The board reposition committed, but its change notification failed.", error);
            if (!occupancy.TryReposition(unitId, origin))
                throw new AggregateException("The board reposition failed and its occupancy could not be restored.", error);
            throw;
        }
    }

    public bool TryFindPath(MapObject body, Vector2D destination, out IReadOnlyList<Vector2D> path)
    {
        path = [];
        Guid unitId = RequireUnit(body);
        if (!TryCurrentCell(body, unitId, out HexCoordinate start) || !Layout.Contains(destination))
            return false;
        HexCoordinate end = Layout.FromWorld(destination);
        if (end != start && (occupancy.Occupants.ContainsKey(end) ||
            occupancy.Reservations.TryGetValue(end, out Guid reservedBy) && reservedBy != unitId))
            return false;
        return TryFindPath(unitId, start, cell => cell == end, out path);
    }

    public bool TryFindPathToRange(MapObject body, MapObject target, double range,
        out IReadOnlyList<Vector2D> path)
    {
        path = [];
        if (!double.IsFinite(range) || range < 0)
            throw new ArgumentOutOfRangeException(nameof(range), "The range must be finite and nonnegative.");
        Guid unitId = RequireUnit(body);
        Guid targetId = RequireUnit(target);
        if (unitId == targetId || !target.IsActive || !target.IsInteractable || !target.IsSelectable ||
            !TryCurrentCell(body, unitId, out HexCoordinate start) ||
            !TryCurrentCell(target, targetId, out HexCoordinate targetCell))
            return false;
        return TryFindPath(unitId, start, cell => cell.DistanceTo(targetCell) <= range, out path);
    }

    private bool TryFindPath(Guid unitId, HexCoordinate start, Func<HexCoordinate, bool> isDestination,
        out IReadOnlyList<Vector2D> path)
    {
        path = [];
        if (isDestination(start)) return true;
        var previous = new Dictionary<HexCoordinate, HexCoordinate> { [start] = start };
        var pending = new Queue<HexCoordinate>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out HexCoordinate current))
        {
            foreach (HexCoordinate next in current.GetNeighbors())
            {
                if (!Layout.Definition.Contains(next) || previous.ContainsKey(next) ||
                    occupancy.Occupants.ContainsKey(next) ||
                    occupancy.Reservations.TryGetValue(next, out Guid reservation) && reservation != unitId)
                    continue;
                previous.Add(next, current);
                if (isDestination(next))
                {
                    var cells = new List<HexCoordinate>();
                    for (HexCoordinate cell = next; cell != start; cell = previous[cell]) cells.Add(cell);
                    cells.Reverse();
                    path = cells.Select(Layout.ToWorld).ToArray();
                    return true;
                }
                pending.Enqueue(next);
            }
        }
        return false;
    }

    public bool TryReserve(MapObject body, Vector2D destination)
    {
        Guid unitId = RequireUnit(body);
        return TryCurrentCell(body, unitId, out _) && Layout.Contains(destination) &&
            occupancy.TryReserveMove(unitId, Layout.FromWorld(destination));
    }

    public bool TryCommit(MapObject body)
    {
        Guid unitId = RequireUnit(body);
        if (!TryCurrentCell(body, unitId, out _) || !occupancy.CommitMove(unitId)) return false;
        body.Position = Layout.ToWorld(occupancy.Positions[unitId]);
        return true;
    }

    public void Cancel(MapObject body) => occupancy.CancelMove(RequireUnit(body));

    void IMapUnitGrid.ValidateAdd(MapObject body)
    {
        if (body.Manager == null) return;
        Guid id = RequireUnit(body);
        if (!occupancy.Positions.TryGetValue(id, out HexCoordinate cell) ||
            body.Position != Layout.ToWorld(cell))
            throw new InvalidOperationException("A unit must occupy its destination cell before joining a hex board.");
    }

    void IMapUnitGrid.ValidatePosition(MapObject body, Vector2D position)
    {
        if (body.Manager == null) return;
        Guid id = RequireUnit(body);
        if (!occupancy.Positions.TryGetValue(id, out HexCoordinate cell) ||
            position != Layout.ToWorld(cell))
            throw new InvalidOperationException("A unit's position must be the projection of its occupied hex cell.");
    }

    void IMapUnitGrid.OnRemoved(MapObject body)
    {
        if (body.Manager != null && Guid.TryParse(body.Id, out Guid unitId)) occupancy.Remove(unitId);
    }

    private bool TryCurrentCell(MapObject body, Guid unitId, out HexCoordinate cell)
    {
        if (!ReferenceEquals(body.MapRuntime, Map) || body.Manager?.ControlledObject != body ||
            !occupancy.Positions.TryGetValue(unitId, out cell))
        {
            cell = default;
            return false;
        }
        if (body.Position != Layout.ToWorld(cell))
            throw new InvalidOperationException("The unit's world position does not match its occupied hex cell.");
        return true;
    }

    private static Guid RequireUnit(MapObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Manager == null || body.Kind == MapObjectKind.Projectile ||
            !Guid.TryParse(body.Id, out Guid id) || id == Guid.Empty)
            throw new ArgumentException("Only a unit map object can occupy a hex cell.", nameof(body));
        return id;
    }
}
