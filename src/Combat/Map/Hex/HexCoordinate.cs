using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Hex;

public enum HexOrientation
{
    PointyTop,
    FlatTop
}

public readonly record struct HexCoordinate(int Q, int R)
{
    private static readonly HexCoordinate[] NeighborOffsets =
    [
        new(1, 0),
        new(1, -1),
        new(0, -1),
        new(-1, 0),
        new(-1, 1),
        new(0, 1),
    ];

    public long DistanceTo(HexCoordinate other)
    {
        long q = (long)Q - other.Q;
        long r = (long)R - other.R;
        return (Math.Abs(q) + Math.Abs(r) + Math.Abs(q + r)) / 2;
    }

    public IEnumerable<HexCoordinate> GetNeighbors()
    {
        foreach (HexCoordinate offset in NeighborOffsets)
        {
            long q = (long)Q + offset.Q;
            long r = (long)R + offset.R;
            if (q is >= int.MinValue and <= int.MaxValue && r is >= int.MinValue and <= int.MaxValue)
                yield return new HexCoordinate((int)q, (int)r);
        }
    }

    public Vector2D ToWorld(double cellSize, HexOrientation orientation)
    {
        ValidateCellSize(cellSize);

        return orientation switch
        {
            HexOrientation.PointyTop => new Vector2D(
                cellSize * Math.Sqrt(3) * (Q + R / 2d),
                cellSize * 1.5 * R),
            HexOrientation.FlatTop => new Vector2D(
                cellSize * 1.5 * Q,
                cellSize * Math.Sqrt(3) * (R + Q / 2d)),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };
    }

    public static HexCoordinate FromWorld(Vector2D position, double cellSize, HexOrientation orientation)
    {
        ValidateCellSize(cellSize);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        (double q, double r) = orientation switch
        {
            HexOrientation.PointyTop =>
                ((Math.Sqrt(3) / 3 * position.X - position.Y / 3) / cellSize,
                    2d / 3 * position.Y / cellSize),
            HexOrientation.FlatTop =>
                (2d / 3 * position.X / cellSize, (-position.X / 3 + Math.Sqrt(3) / 3 * position.Y) / cellSize),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };

        return RoundAxial(q, r);
    }

    private static HexCoordinate RoundAxial(double q, double r)
    {
        double x = q;
        double z = r;
        double y = -x - z;

        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            x < int.MinValue - 1d || x > int.MaxValue + 1d ||
            z < int.MinValue - 1d || z > int.MaxValue + 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(q), "World position is outside the supported axial coordinate range.");
        }

        long roundedX = (long)Math.Round(x, MidpointRounding.AwayFromZero);
        long roundedY = (long)Math.Round(y, MidpointRounding.AwayFromZero);
        long roundedZ = (long)Math.Round(z, MidpointRounding.AwayFromZero);

        double xDifference = Math.Abs(roundedX - x);
        double yDifference = Math.Abs(roundedY - y);
        double zDifference = Math.Abs(roundedZ - z);

        if (xDifference > yDifference && xDifference > zDifference)
        {
            roundedX = -roundedY - roundedZ;
        }
        else if (yDifference > zDifference)
        {
            roundedY = -roundedX - roundedZ;
        }
        else
        {
            roundedZ = -roundedX - roundedY;
        }

        if (roundedX < int.MinValue || roundedX > int.MaxValue ||
            roundedZ < int.MinValue || roundedZ > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(q), "World position is outside the supported axial coordinate range.");
        }

        return new HexCoordinate((int)roundedX, (int)roundedZ);
    }

    private static void ValidateCellSize(double cellSize)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        }
    }
}
