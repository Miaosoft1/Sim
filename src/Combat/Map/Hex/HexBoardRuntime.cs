using System.Collections.ObjectModel;

namespace Miaosoft.Sim.Combat.Map.Hex;

public sealed class HexBoardDefinition
{
    private readonly HashSet<HexCoordinate> playableCells;
    private readonly IReadOnlyList<HexCoordinate> cellView;

    public HexBoardDefinition(IEnumerable<HexCoordinate> playableCells)
    {
        ArgumentNullException.ThrowIfNull(playableCells);

        HexCoordinate[] cells = playableCells
            .OrderBy(cell => cell.Q)
            .ThenBy(cell => cell.R)
            .ToArray();
        if (cells.Length == 0)
        {
            throw new ArgumentException("A hex board must contain at least one playable cell.", nameof(playableCells));
        }
        if (cells.Distinct().Count() != cells.Length)
        {
            throw new ArgumentException("Playable hex cells must be unique.", nameof(playableCells));
        }

        this.playableCells = cells.ToHashSet();
        cellView = Array.AsReadOnly(cells);
    }

    public IReadOnlyList<HexCoordinate> PlayableCells => cellView;

    public bool Contains(HexCoordinate cell) => playableCells.Contains(cell);
}

public sealed class HexBoardRuntime
{
    private readonly Dictionary<HexCoordinate, Guid> unitsByCell = [];
    private readonly Dictionary<Guid, HexCoordinate> cellsByUnit = [];
    private readonly Dictionary<Guid, MoveReservation> movesByUnit = [];
    private readonly Dictionary<HexCoordinate, Guid> unitsByReservedCell = [];
    private readonly ReadOnlyDictionary<HexCoordinate, Guid> occupantView;
    private readonly ReadOnlyDictionary<Guid, HexCoordinate> positionView;
    private readonly ReadOnlyDictionary<HexCoordinate, Guid> reservationView;

    public HexBoardRuntime(HexBoardDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        occupantView = new(unitsByCell);
        positionView = new(cellsByUnit);
        reservationView = new(unitsByReservedCell);
    }

    public HexBoardDefinition Definition { get; }
    public IReadOnlyDictionary<HexCoordinate, Guid> Occupants => occupantView;
    public IReadOnlyDictionary<Guid, HexCoordinate> Positions => positionView;
    public IReadOnlyDictionary<HexCoordinate, Guid> Reservations => reservationView;
    public bool HasReservation(Guid unitId) => movesByUnit.ContainsKey(unitId);

    public bool TryPlace(Guid unitId, HexCoordinate cell)
    {
        if (unitId == Guid.Empty || !Definition.Contains(cell) ||
            cellsByUnit.ContainsKey(unitId) || unitsByCell.ContainsKey(cell) ||
            unitsByReservedCell.ContainsKey(cell))
        {
            return false;
        }

        unitsByCell.Add(cell, unitId);
        cellsByUnit.Add(unitId, cell);
        return true;
    }

    public bool TryReserveMove(Guid unitId, HexCoordinate destination)
    {
        if (!cellsByUnit.TryGetValue(unitId, out HexCoordinate origin) ||
            movesByUnit.ContainsKey(unitId) ||
            origin.DistanceTo(destination) != 1 ||
            !Definition.Contains(destination) ||
            unitsByCell.ContainsKey(destination) ||
            unitsByReservedCell.ContainsKey(destination))
        {
            return false;
        }

        movesByUnit.Add(unitId, new MoveReservation(origin, destination));
        unitsByReservedCell.Add(destination, unitId);
        return true;
    }

    public bool CommitMove(Guid unitId)
    {
        if (!movesByUnit.TryGetValue(unitId, out MoveReservation reservation) ||
            !cellsByUnit.TryGetValue(unitId, out HexCoordinate current) ||
            current != reservation.Origin ||
            !unitsByCell.TryGetValue(reservation.Origin, out Guid originOwner) ||
            originOwner != unitId ||
            !unitsByReservedCell.TryGetValue(reservation.Destination, out Guid reservedBy) ||
            reservedBy != unitId ||
            unitsByCell.ContainsKey(reservation.Destination))
        {
            return false;
        }

        unitsByCell.Remove(reservation.Origin);
        unitsByCell.Add(reservation.Destination, unitId);
        cellsByUnit[unitId] = reservation.Destination;
        RemoveReservation(unitId, reservation);
        return true;
    }

    public bool CancelMove(Guid unitId)
    {
        if (!movesByUnit.Remove(unitId, out MoveReservation reservation))
        {
            return false;
        }

        unitsByReservedCell.Remove(reservation.Destination);
        return true;
    }

    public bool Remove(Guid unitId)
    {
        if (!cellsByUnit.Remove(unitId, out HexCoordinate cell))
        {
            CancelMove(unitId);
            return false;
        }

        if (!unitsByCell.Remove(cell, out Guid occupant) || occupant != unitId)
        {
            throw new InvalidOperationException("Hex board occupancy is inconsistent.");
        }

        CancelMove(unitId);
        return true;
    }

    public bool TryReposition(Guid unitId, HexCoordinate destination)
    {
        if (!cellsByUnit.TryGetValue(unitId, out HexCoordinate origin) ||
            !unitsByCell.TryGetValue(origin, out Guid owner) || owner != unitId ||
            movesByUnit.ContainsKey(unitId) || !Definition.Contains(destination))
            return false;
        if (origin == destination) return true;
        if (unitsByCell.ContainsKey(destination) || unitsByReservedCell.ContainsKey(destination))
            return false;

        unitsByCell.Remove(origin);
        unitsByCell.Add(destination, unitId);
        cellsByUnit[unitId] = destination;
        return true;
    }

    public bool TryTransferTo(HexBoardRuntime destinationBoard, Guid unitId, HexCoordinate destination)
    {
        ArgumentNullException.ThrowIfNull(destinationBoard);
        if (ReferenceEquals(this, destinationBoard) ||
            !cellsByUnit.TryGetValue(unitId, out HexCoordinate sourceCell) ||
            !unitsByCell.TryGetValue(sourceCell, out Guid sourceOwner) ||
            sourceOwner != unitId ||
            !destinationBoard.CanAccept(unitId, destination))
        {
            return false;
        }

        CancelMove(unitId);
        if (!unitsByCell.Remove(sourceCell))
        {
            throw new InvalidOperationException("Hex board occupancy is inconsistent.");
        }
        cellsByUnit.Remove(unitId);
        destinationBoard.unitsByCell.Add(destination, unitId);
        destinationBoard.cellsByUnit.Add(unitId, destination);
        return true;
    }

    private bool CanAccept(Guid unitId, HexCoordinate destination) =>
        unitId != Guid.Empty &&
        Definition.Contains(destination) &&
        !cellsByUnit.ContainsKey(unitId) &&
        !unitsByCell.ContainsKey(destination) &&
        !unitsByReservedCell.ContainsKey(destination);

    private void RemoveReservation(Guid unitId, MoveReservation reservation)
    {
        movesByUnit.Remove(unitId);
        if (unitsByReservedCell.TryGetValue(reservation.Destination, out Guid reservedBy) &&
            reservedBy == unitId)
        {
            unitsByReservedCell.Remove(reservation.Destination);
        }
    }

    private readonly record struct MoveReservation(HexCoordinate Origin, HexCoordinate Destination);
}
