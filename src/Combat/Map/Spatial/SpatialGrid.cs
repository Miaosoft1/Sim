using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map.Spatial;

public sealed class SpatialGrid<T> where T : notnull
{
    /// <summary>Counts visits, examined entries and unique candidates, including conservative overflow entries.</summary>
    public readonly record struct QueryStatistics(long CellsVisited, long EntriesExamined, int Candidates);

    private readonly record struct Cell(long X, long Y);
    private readonly record struct Interval(double Min, double Max);
    private readonly record struct CellRange(long Min, long Max);

    private sealed class Entry(T item, Vector2D position, Cell? cell, long order)
    {
        public T Item { get; private set; } = item;
        public Vector2D Position { get; set; } = position;
        public Cell? Cell { get; set; } = cell;
        public long Order { get; } = order;
        public bool Active { get; private set; } = true;

        public void Release()
        {
            Active = false;
            Item = default!;
        }
    }

    public const double DefaultCellSize = 64;

    private readonly MapSpace space;
    private readonly Dictionary<Cell, HashSet<T>> buckets = [];
    private readonly Dictionary<T, Entry> entries;
    private readonly List<Entry> orderedEntries = [];
    private readonly HashSet<T> overflow;
    private long nextOrder;

    public SpatialGrid(MapSpace space, double cellSize = DefaultCellSize, IEqualityComparer<T>? comparer = null)
    {
        this.space = space ?? throw new ArgumentNullException(nameof(space));
        if (!double.IsFinite(cellSize) || cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        CellSize = cellSize;
        entries = new(comparer);
        overflow = new(comparer);
    }

    public double CellSize { get; }
    public int Count => entries.Count;

    public void Add(T item, Vector2D position)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidatePosition(position);
        Cell? cell = GetCell(position);
        var entry = new Entry(item, position, cell, nextOrder++);
        entries.Add(item, entry);
        orderedEntries.Add(entry);
        AddToCell(item, cell);
    }

    public void Update(T item, Vector2D position)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidatePosition(position);
        if (!entries.TryGetValue(item, out Entry? previous))
            throw new InvalidOperationException("The object is not in the spatial grid.");
        Cell? cell = GetCell(position);
        if (cell != previous.Cell)
        {
            RemoveFromCell(item, previous.Cell);
            AddToCell(item, cell);
        }
        previous.Position = position;
        previous.Cell = cell;
    }

    public bool Remove(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!entries.Remove(item, out Entry? previous)) return false;
        RemoveFromCell(item, previous.Cell);
        previous.Release();
        if (orderedEntries.Count >= 128 && (long)entries.Count * 2 < orderedEntries.Count)
            orderedEntries.RemoveAll(entry => !entry.Active);
        return true;
    }

    public IReadOnlyList<T> QueryBox(Vector2D minimum, Vector2D maximum) =>
        QueryBox(minimum, maximum, out _);

    public bool PrefersOrderedScan(Vector2D minimum, Vector2D maximum)
    {
        Span<CellRange> xCells = stackalloc CellRange[2];
        Span<CellRange> yCells = stackalloc CellRange[2];
        GetCellRanges(minimum, maximum, xCells, yCells, out int xCount, out int yCount);
        return PreferOrderedScan(xCells, xCount, yCells, yCount);
    }

    public IReadOnlyList<T> QueryBox(Vector2D minimum, Vector2D maximum, out QueryStatistics statistics)
    {
        Span<CellRange> xCells = stackalloc CellRange[2];
        Span<CellRange> yCells = stackalloc CellRange[2];
        GetCellRanges(minimum, maximum, xCells, yCells, out int xCount, out int yCount);
        if (PreferOrderedScan(xCells, xCount, yCells, yCount))
        {
            var ordered = new List<T>();
            foreach (Entry entry in orderedEntries)
            {
                if (!entry.Active) continue;
                Cell? cell = entry.Cell;
                if (cell == null || InRanges(cell.Value.X, xCells, xCount) && InRanges(cell.Value.Y, yCells, yCount))
                    ordered.Add(entry.Item);
            }
            statistics = new(0, orderedEntries.Count, ordered.Count);
            return ordered;
        }

        var result = new HashSet<T>(entries.Comparer);
        result.UnionWith(overflow);
        long cellsVisited = 0;
        long entriesExamined = overflow.Count;
        for (int x = 0; x < xCount; x++)
        for (int y = 0; y < yCount; y++)
        {
            cellsVisited += CollectCells(xCells[x].Min, xCells[x].Max, yCells[y].Min, yCells[y].Max,
                result, out int examined);
            entriesExamined += examined;
        }
        var candidates = result.ToList();
        candidates.Sort((left, right) => entries[left].Order.CompareTo(entries[right].Order));
        statistics = new(cellsVisited, entriesExamined, candidates.Count);
        return candidates;
    }

    /// <summary>The predicate may be evaluated more than once for wrapped or overlapping ranges.</summary>
    public bool AnyInBox(Vector2D minimum, Vector2D maximum, Predicate<T> predicate)
    {
        ValidateBounds(minimum, maximum);
        ArgumentNullException.ThrowIfNull(predicate);
        if (entries.Count <= 8)
        {
            foreach (T item in entries.Keys)
                if (predicate(item)) return true;
            return false;
        }
        foreach (T item in overflow)
            if (predicate(item)) return true;
        Span<Interval> xs = stackalloc Interval[2];
        Span<Interval> ys = stackalloc Interval[2];
        int xCount = SplitAxis(minimum.X, maximum.X, space.IsWrapped ? space.Width : null, xs);
        int yCount = SplitAxis(minimum.Y, maximum.Y, space.IsWrapped ? space.Height : null, ys);
        for (int x = 0; x < xCount; x++)
        for (int y = 0; y < yCount; y++)
            if (AnyInCells(ToCellBound(xs[x].Min), ToCellBound(xs[x].Max),
                    ToCellBound(ys[y].Min), ToCellBound(ys[y].Max), predicate))
                return true;
        return false;
    }

    public bool TryFindNearest(Vector2D origin, Predicate<T> predicate, Comparison<T> tieBreak, out T result)
    {
        ValidatePosition(origin);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(tieBreak);
        bool found = false;
        T nearest = default!;
        double best = double.PositiveInfinity;
        if (entries.Count <= 32)
        {
            foreach (Entry entry in orderedEntries)
                if (entry.Active) ConsiderEntry(entry);
        }
        else
        {
            foreach (T item in overflow) Consider(item);
            if (buckets.Count <= 16)
            {
                var bucketQueue = new PriorityQueue<HashSet<T>, double>(
                    buckets.Select(pair => (pair.Value, DistanceToCell(origin, pair.Key))));
                while (bucketQueue.TryDequeue(out HashSet<T>? bucket, out double lowerBound))
                {
                    if (found && lowerBound > best) break;
                    foreach (T item in bucket) Consider(item);
                }
            }
            else if (GetCell(origin) is { } start && TryGetWrappedCellCounts(out long widthCells, out long heightCells))
            {
                // Bound empty-cell exploration before falling back to the remaining occupied cells.
                var pending = new PriorityQueue<Cell, double>();
                var discovered = new HashSet<Cell>();
                var processed = new HashSet<Cell>();
                Enqueue(start);
                while (pending.TryDequeue(out Cell cell, out double lowerBound))
                {
                    if (found && lowerBound > best) break;
                    processed.Add(cell);
                    if (buckets.TryGetValue(cell, out HashSet<T>? bucket))
                        foreach (T item in bucket) Consider(item);
                    if (processed.Count >= (found ? 64 : 16))
                    {
                        ScanRemaining(processed);
                        break;
                    }
                    if (space.IsWrapped)
                    {
                        Enqueue(new(cell.X == 0 ? widthCells - 1 : cell.X - 1, cell.Y));
                        Enqueue(new(cell.X == widthCells - 1 ? 0 : cell.X + 1, cell.Y));
                        Enqueue(new(cell.X, cell.Y == 0 ? heightCells - 1 : cell.Y - 1));
                        Enqueue(new(cell.X, cell.Y == heightCells - 1 ? 0 : cell.Y + 1));
                    }
                    else
                    {
                        if (cell.X != long.MinValue) Enqueue(new(cell.X - 1, cell.Y));
                        if (cell.X != long.MaxValue) Enqueue(new(cell.X + 1, cell.Y));
                        if (cell.Y != long.MinValue) Enqueue(new(cell.X, cell.Y - 1));
                        if (cell.Y != long.MaxValue) Enqueue(new(cell.X, cell.Y + 1));
                    }
                }

                void Enqueue(Cell cell)
                {
                    if (discovered.Add(cell)) pending.Enqueue(cell, DistanceToCell(origin, cell));
                }
            }
            else
            {
                ScanRemaining(null);
            }
        }

        result = nearest;
        return found;

        void ScanRemaining(HashSet<Cell>? processed)
        {
            if (!found)
            {
                foreach (Entry entry in orderedEntries)
                    if (entry.Active && entry.Cell is { } cell && processed?.Contains(cell) != true)
                        ConsiderEntry(entry);
                return;
            }
            foreach (var (cell, bucket) in buckets)
            {
                if (processed?.Contains(cell) == true || found && DistanceToCell(origin, cell) > best) continue;
                foreach (T item in bucket) Consider(item);
            }
        }

        void Consider(T item)
        {
            ConsiderEntry(entries[item]);
        }

        void ConsiderEntry(Entry entry)
        {
            if (!predicate(entry.Item)) return;
            double distance = space.Distance(origin, entry.Position);
            if (!found || distance < best || distance == best && tieBreak(entry.Item, nearest) < 0)
            {
                found = true;
                best = distance;
                nearest = entry.Item;
            }
        }
    }

    private bool TryGetWrappedCellCounts(out long widthCells, out long heightCells)
    {
        widthCells = heightCells = 0;
        if (!space.IsWrapped) return true;
        double width = Math.Ceiling(space.Width / CellSize);
        double height = Math.Ceiling(space.Height / CellSize);
        const double limit = 9223372036854775808d;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width >= limit || height >= limit) return false;
        widthCells = (long)width;
        heightCells = (long)height;
        return true;
    }

    private static void ValidatePosition(Vector2D position)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position), "Spatial positions must be finite.");
    }

    private static bool InRanges(long coordinate, ReadOnlySpan<CellRange> ranges, int count)
    {
        for (int index = 0; index < count; index++)
            if (coordinate >= ranges[index].Min && coordinate <= ranges[index].Max) return true;
        return false;
    }

    private static void ValidateBounds(Vector2D minimum, Vector2D maximum)
    {
        if (double.IsNaN(minimum.X) || double.IsNaN(minimum.Y) ||
            double.IsNaN(maximum.X) || double.IsNaN(maximum.Y) ||
            minimum.X > maximum.X || minimum.Y > maximum.Y)
            throw new ArgumentOutOfRangeException(nameof(minimum), "The query bounds must be ordered and not NaN.");
    }

    private void GetCellRanges(Vector2D minimum, Vector2D maximum, Span<CellRange> xCells, Span<CellRange> yCells,
        out int xCount, out int yCount)
    {
        ValidateBounds(minimum, maximum);
        Span<Interval> xs = stackalloc Interval[2];
        Span<Interval> ys = stackalloc Interval[2];
        xCount = SplitAxis(minimum.X, maximum.X, space.IsWrapped ? space.Width : null, xs);
        yCount = SplitAxis(minimum.Y, maximum.Y, space.IsWrapped ? space.Height : null, ys);
        for (int x = 0; x < xCount; x++)
            xCells[x] = new(ToCellBound(xs[x].Min), ToCellBound(xs[x].Max));
        for (int y = 0; y < yCount; y++)
            yCells[y] = new(ToCellBound(ys[y].Min), ToCellBound(ys[y].Max));
    }

    private bool PreferOrderedScan(ReadOnlySpan<CellRange> xCells, int xCount, ReadOnlySpan<CellRange> yCells, int yCount)
    {
        double cellCount = 0;
        for (int x = 0; x < xCount; x++)
        for (int y = 0; y < yCount; y++)
            cellCount += ((double)xCells[x].Max - xCells[x].Min + 1) *
                ((double)yCells[y].Max - yCells[y].Min + 1);
        return entries.Count <= 32 || cellCount >= buckets.Count * .1;
    }

    private Cell? GetCell(Vector2D position)
    {
        double x = space.IsWrapped ? Normalize(position.X, space.Width) : position.X;
        double y = space.IsWrapped ? Normalize(position.Y, space.Height) : position.Y;
        double cellX = CellCoordinate(x), cellY = CellCoordinate(y);
        const double limit = 9223372036854775808d;
        if (!double.IsFinite(cellX) || !double.IsFinite(cellY) ||
            cellX < -limit || cellX >= limit || cellY < -limit || cellY >= limit)
            return null;
        return new((long)cellX, (long)cellY);
    }

    private static double Normalize(double value, double period)
    {
        double result = Vector2D.Wrap(value, period);
        return result >= period ? 0 : result;
    }

    private static int SplitAxis(double minimum, double maximum, double? period, Span<Interval> result)
    {
        minimum = Math.BitDecrement(minimum);
        maximum = Math.BitIncrement(maximum);
        if (period is not double width)
        {
            result[0] = new(minimum, maximum);
            return 1;
        }
        double span = maximum - minimum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) ||
            !double.IsFinite(span) || span >= width)
        {
            result[0] = new(0, width);
            return 1;
        }
        double start = Normalize(minimum, width);
        double end = Normalize(maximum, width);
        if (start > end)
        {
            result[0] = new(start, width);
            result[1] = new(0, end);
            return 2;
        }
        result[0] = start == end && span > 0 ? new(0, width) : new(start, end);
        return 1;
    }

    private long ToCellBound(double value)
    {
        double cell = CellCoordinate(value);
        const double limit = 9223372036854775808d;
        if (cell <= -limit) return long.MinValue;
        if (cell >= limit) return long.MaxValue;
        return (long)cell;
    }

    private double CellCoordinate(double value)
    {
        double scaled = value / CellSize;
        return scaled == 0 && value < 0 ? -1 : Math.Floor(scaled);
    }

    private void AddToCell(T item, Cell? cell)
    {
        if (cell is not { } key)
        {
            overflow.Add(item);
            return;
        }
        if (!buckets.TryGetValue(key, out HashSet<T>? bucket))
            buckets.Add(key, bucket = new(entries.Comparer));
        bucket.Add(item);
    }

    private void RemoveFromCell(T item, Cell? cell)
    {
        if (cell is not { } key)
        {
            overflow.Remove(item);
            return;
        }
        HashSet<T> bucket = buckets[key];
        bucket.Remove(item);
        if (bucket.Count == 0) buckets.Remove(key);
    }

    private int CollectCells(long minX, long maxX, long minY, long maxY, HashSet<T> result, out int entriesExamined)
    {
        entriesExamined = 0;
        double cellCount = ((double)maxX - minX + 1) * ((double)maxY - minY + 1);
        if (cellCount <= Math.Max(32, buckets.Count * 2d) && cellCount <= 100_000)
        {
            for (long x = minX; ; x++)
            {
                for (long y = minY; ; y++)
                {
                    if (buckets.TryGetValue(new(x, y), out HashSet<T>? bucket))
                    {
                        entriesExamined += bucket.Count;
                        result.UnionWith(bucket);
                    }
                    if (y == maxY) break;
                }
                if (x == maxX) break;
            }
            return (int)cellCount;
        }

        foreach (var (cell, bucket) in buckets)
            if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY)
            {
                entriesExamined += bucket.Count;
                result.UnionWith(bucket);
            }
        return buckets.Count;
    }

    private bool AnyInCells(long minX, long maxX, long minY, long maxY, Predicate<T> predicate)
    {
        double cellCount = ((double)maxX - minX + 1) * ((double)maxY - minY + 1);
        if (cellCount <= Math.Max(32, buckets.Count * 2d) && cellCount <= 100_000)
        {
            for (long x = minX; ; x++)
            {
                for (long y = minY; ; y++)
                {
                    if (buckets.TryGetValue(new(x, y), out HashSet<T>? bucket))
                        foreach (T item in bucket)
                            if (predicate(item)) return true;
                    if (y == maxY) break;
                }
                if (x == maxX) break;
            }
            return false;
        }
        foreach (var (cell, bucket) in buckets)
            if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY)
                foreach (T item in bucket)
                    if (predicate(item)) return true;
        return false;
    }

    private double DistanceToCell(Vector2D origin, Cell cell)
    {
        double minX = Math.BitDecrement(cell.X * CellSize);
        double maxX = Math.BitIncrement(((double)cell.X + 1) * CellSize);
        double minY = Math.BitDecrement(cell.Y * CellSize);
        double maxY = Math.BitIncrement(((double)cell.Y + 1) * CellSize);
        double x = origin.X, y = origin.Y;
        if (space.IsWrapped)
        {
            x = Normalize(x, space.Width);
            y = Normalize(y, space.Height);
            minX = Math.Max(0, minX);
            maxX = Math.Min(space.Width, maxX);
            minY = Math.Max(0, minY);
            maxY = Math.Min(space.Height, maxY);
        }
        double dx = AxisDistance(x, minX, maxX, space.IsWrapped ? space.Width : null);
        double dy = AxisDistance(y, minY, maxY, space.IsWrapped ? space.Height : null);
        double distance = Math.Sqrt(dx * dx + dy * dy);
        return double.IsFinite(distance) && distance > 0 ? Math.BitDecrement(distance) : 0;
    }

    private static double AxisDistance(double point, double minimum, double maximum, double? period)
    {
        static double Distance(double value, double min, double max) =>
            value < min ? min - value : value > max ? value - max : 0;
        double result = Distance(point, minimum, maximum);
        if (period is double width)
            result = Math.Min(result, Math.Min(Distance(point - width, minimum, maximum),
                Distance(point + width, minimum, maximum)));
        return result;
    }
}
