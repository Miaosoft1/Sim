using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Hex;

public sealed class HexBoardLayout : IMapLineTargetingMetric
{
    public HexBoardLayout(HexBoardDefinition definition, double cellSize, HexOrientation orientation)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (!double.IsFinite(cellSize) || cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (!Enum.IsDefined(orientation)) throw new ArgumentOutOfRangeException(nameof(orientation));
        CellSize = cellSize;
        Orientation = orientation;
        Vector2D[] centers = definition.PlayableCells.Select(cell => cell.ToWorld(cellSize, orientation)).ToArray();
        double minX = centers.Min(center => center.X);
        double minY = centers.Min(center => center.Y);
        Width = centers.Max(center => center.X) - minX + 2 * cellSize;
        Height = centers.Max(center => center.Y) - minY + 2 * cellSize;
        Origin = new Vector2D(cellSize - minX, cellSize - minY);
        if (!double.IsFinite(Width) || !double.IsFinite(Height) ||
            !double.IsFinite(Origin.X) || !double.IsFinite(Origin.Y))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "The projected board is too large.");
    }

    public HexBoardDefinition Definition { get; }
    public MapTargetingDistanceUnit DistanceUnit => MapTargetingDistanceUnit.Hex;
    public double CellSize { get; }
    public HexOrientation Orientation { get; }
    public Vector2D Origin { get; }
    public double Width { get; }
    public double Height { get; }

    public Vector2D ToWorld(HexCoordinate cell)
    {
        Vector2D projected = cell.ToWorld(CellSize, Orientation);
        return new Vector2D(projected.X + Origin.X, projected.Y + Origin.Y);
    }

    public HexCoordinate FromWorld(Vector2D position) =>
        HexCoordinate.FromWorld(new Vector2D(position.X - Origin.X, position.Y - Origin.Y), CellSize, Orientation);

    public bool Contains(Vector2D position)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            position.X < 0 || position.X > Width || position.Y < 0 || position.Y > Height)
            return false;
        try
        {
            return Definition.Contains(FromWorld(position));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public double Distance(Vector2D first, Vector2D second) => FromWorld(first).DistanceTo(FromWorld(second));

    public double ToWorldLineLength(Vector2D direction, double range)
    {
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) ||
            !double.IsFinite(range) || range < 0)
            throw new ArgumentOutOfRangeException(nameof(range), "Line direction and range must be finite.");
        (double q, double r) = Orientation switch
        {
            HexOrientation.PointyTop =>
                ((Math.Sqrt(3) / 3 * direction.X - direction.Y / 3) / CellSize,
                    2d / 3 * direction.Y / CellSize),
            HexOrientation.FlatTop =>
                (2d / 3 * direction.X / CellSize,
                    (-direction.X / 3 + Math.Sqrt(3) / 3 * direction.Y) / CellSize),
            _ => throw new InvalidOperationException("The board orientation is invalid.")
        };
        double steps = Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(q + r)));
        double length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (steps <= 0 || !double.IsFinite(steps) || !double.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(direction), "Line direction must be finite and nonzero.");
        double result = length / steps * range;
        if (!double.IsFinite(result)) throw new ArgumentOutOfRangeException(nameof(range));
        return result;
    }

    public double ToWorldLineWidth(double width)
    {
        double result = Math.Sqrt(3) * CellSize * width;
        if (!double.IsFinite(width) || width < 0 || !double.IsFinite(result))
            throw new ArgumentOutOfRangeException(nameof(width));
        return result;
    }
}
