using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapDefinition
    {
        private readonly Lazy<(Vector2D[] Points, List<(int Target, double Distance)>[] Edges)> navigation;
        public MapDefinition(
            string id,
            string name,
            double width,
            double height,
            string type = "Overworld",
            string? boundaryType = null,
            IEnumerable<MapDungeon>? dungeons = null,
            IEnumerable<MapRoomDefinition>? rooms = null,
            IEnumerable<MapCorridorDefinition>? corridors = null,
            bool tradeIsolated = false,
            bool keepSourceVisible = false,
            bool appearanceOverlay = false,
            double saturation = 1,
            double brightness = 1,
            bool isMainPlane = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            if (width <= 0 || !double.IsFinite(width))
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height <= 0 || !double.IsFinite(height))
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            Id = id;
            Name = name;
            Width = width;
            Height = height;
            Type = string.IsNullOrWhiteSpace(type) ? "Overworld" : type;
            BoundaryType = !string.IsNullOrWhiteSpace(boundaryType)
                ? boundaryType
                : (string.Equals(Type, "Interior", StringComparison.OrdinalIgnoreCase) ? "Interior" : "Torus");
            Dungeons = Array.AsReadOnly(dungeons?.ToArray() ?? []);
            Rooms = Array.AsReadOnly(rooms?.ToArray() ?? []);
            Corridors = Array.AsReadOnly(corridors?.ToArray() ?? []);
            if (!double.IsFinite(saturation) || saturation < 0 || !double.IsFinite(brightness) || brightness < 0)
                throw new ArgumentOutOfRangeException(nameof(saturation));
            TradeIsolated = tradeIsolated;
            KeepSourceVisible = keepSourceVisible;
            AppearanceOverlay = appearanceOverlay;
            Saturation = saturation;
            Brightness = brightness;
            IsMainPlane = isMainPlane;
            ValidateGeometry();
            navigation = new(BuildNavigation);
        }

        public string Id { get; }
        public string Name { get; }
        public double Width { get; }
        public double Height { get; }
        public string Type { get; }
        public string BoundaryType { get; }
        public bool TradeIsolated { get; }
        public bool KeepSourceVisible { get; }
        public bool AppearanceOverlay { get; }
        public double Saturation { get; }
        public double Brightness { get; }
        public bool IsMainPlane { get; }
        public IReadOnlyList<MapDungeon> Dungeons { get; }
        public IReadOnlyList<MapRoomDefinition> Rooms { get; }
        public IReadOnlyList<MapCorridorDefinition> Corridors { get; }

        public bool IsInterior => string.Equals(Type, "Interior", StringComparison.OrdinalIgnoreCase);
        public bool IsTorus => string.Equals(BoundaryType, "Torus", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(BoundaryType, "Spherical", StringComparison.OrdinalIgnoreCase);

        public bool IsWalkable(Vector2D point)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
            if (!IsInterior)
            {
                return true;
            }

            return Rooms.Any(r => r.Contains(point)) || Corridors.Any(c => c.Contains(point));
        }

        public (Vector2D Position, bool Blocked) ClipMovement(Vector2D start, Vector2D end)
        {
            if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
                !double.IsFinite(end.X) || !double.IsFinite(end.Y))
                throw new ArgumentOutOfRangeException(nameof(end));
            if (!IsInterior) return (end, false);
            if (!IsWalkable(start)) return (start, true);
            double dx = end.X - start.X, dy = end.Y - start.Y;
            var intervals = new List<(double Start, double End)>();
            foreach (var room in Rooms)
                AddRectangle(start.X - room.X, start.Y - room.Y, dx, dy, 0, room.Width, 0, room.Height);
            foreach (var corridor in Corridors)
            {
                double cx = corridor.EndX - corridor.StartX, cy = corridor.EndY - corridor.StartY;
                double length = Math.Sqrt(cx * cx + cy * cy);
                double sx = start.X - corridor.StartX, sy = start.Y - corridor.StartY;
                if (length <= 0.001)
                {
                    double a = dx * dx + dy * dy;
                    double b = 2 * (sx * dx + sy * dy);
                    double c = sx * sx + sy * sy - corridor.Width * corridor.Width / 4;
                    if (a == 0) { if (c <= 0) intervals.Add((0, 1)); }
                    else
                    {
                        double disc = b * b - 4 * a * c;
                        if (disc >= 0)
                            intervals.Add((Math.Max(0, (-b - Math.Sqrt(disc)) / (2 * a)),
                                Math.Min(1, (-b + Math.Sqrt(disc)) / (2 * a))));
                    }
                    continue;
                }
                cx /= length; cy /= length;
                AddRectangle(sx * cx + sy * cy, -sx * cy + sy * cx,
                    dx * cx + dy * cy, -dx * cy + dy * cx,
                    0, length, -corridor.Width / 2, corridor.Width / 2);
            }
            double reachable = 0;
            foreach (var interval in intervals.OrderBy(value => value.Start))
            {
                if (interval.End < interval.Start) continue;
                if (interval.Start > reachable + 1e-10) break;
                reachable = Math.Max(reachable, interval.End);
            }
            reachable = Math.Clamp(reachable, 0, 1);
            return (new Vector2D(start.X + dx * reachable, start.Y + dy * reachable), reachable < 1 - 1e-10);

            void AddRectangle(double x, double y, double vx, double vy,
                double left, double right, double top, double bottom)
            {
                double lo = 0, hi = 1;
                if (Clip(x, vx, left, right, ref lo, ref hi) &&
                    Clip(y, vy, top, bottom, ref lo, ref hi)) intervals.Add((lo, hi));
            }
            static bool Clip(double origin, double direction, double min, double max, ref double lo, ref double hi)
            {
                if (Math.Abs(direction) < 1e-15) return origin >= min && origin <= max;
                double a = (min - origin) / direction, b = (max - origin) / direction;
                lo = Math.Max(lo, Math.Min(a, b));
                hi = Math.Min(hi, Math.Max(a, b));
                return lo <= hi;
            }
        }

        public IReadOnlyList<Vector2D>? FindPath(Vector2D start, Vector2D destination)
        {
            if (!IsWalkable(start) || !IsWalkable(destination))
            {
                return null;
            }
            if (!ClipMovement(start, destination).Blocked) return new[] { destination };
            var (points, edges) = navigation.Value;
            int count = points.Length;
            double[] distances = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
            int[] previous = Enumerable.Repeat(-1, count).ToArray();
            var queue = new PriorityQueue<int, double>();
            for (int i = 0; i < count; i++)
                if (!ClipMovement(start, points[i]).Blocked)
                {
                    distances[i] = start.DistanceTo(points[i]);
                    queue.Enqueue(i, distances[i]);
                }
            int end = -1;
            double best = double.PositiveInfinity;
            while (queue.TryDequeue(out int current, out double distance))
            {
                if (distance > distances[current] || distance >= best) continue;
                if (!ClipMovement(points[current], destination).Blocked)
                {
                    double total = distance + points[current].DistanceTo(destination);
                    if (total < best) { best = total; end = current; }
                }
                foreach (var edge in edges[current])
                {
                    double next = distance + edge.Distance;
                    if (next >= distances[edge.Target]) continue;
                    distances[edge.Target] = next;
                    previous[edge.Target] = current;
                    queue.Enqueue(edge.Target, next);
                }
            }
            if (end < 0) return null;
            var result = new List<Vector2D> { destination };
            while (end >= 0) { result.Add(points[end]); end = previous[end]; }
            result.Reverse();
            for (int i = 0; i < result.Count - 1;)
            {
                var from = i == 0 ? start : result[i - 1];
                if (!ClipMovement(from, result[i + 1]).Blocked) result.RemoveAt(i);
                else i++;
            }
            return result.AsReadOnly();
        }

        private (Vector2D[] Points, List<(int Target, double Distance)>[] Edges) BuildNavigation()
        {
            var points = Rooms.SelectMany(room => new[] { room.Center,
                new Vector2D(room.X, room.Y), new Vector2D(room.X + room.Width, room.Y),
                new Vector2D(room.X, room.Y + room.Height), new Vector2D(room.X + room.Width, room.Y + room.Height) })
                .Concat(Corridors.SelectMany(c => new[] { c.Start, c.End })).ToList();
            var polygons = Rooms.Select(room => new[] {
                new Vector2D(room.X, room.Y), new Vector2D(room.X + room.Width, room.Y),
                new Vector2D(room.X + room.Width, room.Y + room.Height), new Vector2D(room.X, room.Y + room.Height)
            }).ToList();
            foreach (var corridor in Corridors)
            {
                double dx = corridor.EndX - corridor.StartX, dy = corridor.EndY - corridor.StartY;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0.001) continue;
                double x = -dy / length * corridor.Width / 2, y = dx / length * corridor.Width / 2;
                var polygon = new[] {
                    new Vector2D(corridor.StartX + x, corridor.StartY + y),
                    new Vector2D(corridor.EndX + x, corridor.EndY + y),
                    new Vector2D(corridor.EndX - x, corridor.EndY - y),
                    new Vector2D(corridor.StartX - x, corridor.StartY - y)
                };
                polygons.Add(polygon);
                points.AddRange(polygon);
            }
            // Union boundaries acquire additional corners where room and corridor edges intersect.
            for (int i = 0; i < polygons.Count; i++)
                for (int j = i + 1; j < polygons.Count; j++)
                    for (int a = 0; a < 4; a++)
                        for (int b = 0; b < 4; b++)
                        {
                            var p = polygons[i][a]; var q = polygons[j][b];
                            var endP = polygons[i][(a + 1) % 4]; var endQ = polygons[j][(b + 1) % 4];
                            var r = new Vector2D(endP.X - p.X, endP.Y - p.Y);
                            var s = new Vector2D(endQ.X - q.X, endQ.Y - q.Y);
                            double cross = r.X * s.Y - r.Y * s.X;
                            if (Math.Abs(cross) < 1e-10) continue;
                            var offset = new Vector2D(q.X - p.X, q.Y - p.Y);
                            double t = (offset.X * s.Y - offset.Y * s.X) / cross;
                            double u = (offset.X * r.Y - offset.Y * r.X) / cross;
                            if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                                points.Add(new(p.X + t * r.X, p.Y + t * r.Y));
                        }
            for (int i = 0; i < Rooms.Count; i++)
                for (int j = i + 1; j < Rooms.Count; j++)
                {
                    var a = Rooms[i]; var b = Rooms[j];
                    double left = Math.Max(a.X, b.X), right = Math.Min(a.X + a.Width, b.X + b.Width);
                    double top = Math.Max(a.Y, b.Y), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
                    if (right >= left && bottom >= top && (right > left || bottom > top))
                        points.Add(new((left + right) / 2, (top + bottom) / 2));
                }
            var vertices = points.Distinct().ToArray();
            var edges = vertices.Select(_ => new List<(int, double)>()).ToArray();
            for (int i = 0; i < vertices.Length; i++)
                for (int j = i + 1; j < vertices.Length; j++)
                    if (!ClipMovement(vertices[i], vertices[j]).Blocked)
                    {
                        double distance = vertices[i].DistanceTo(vertices[j]);
                        edges[i].Add((j, distance));
                        edges[j].Add((i, distance));
                    }
            return (vertices, edges);
        }

        private void ValidateGeometry()
        {
            if (Rooms.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != Rooms.Count ||
                Corridors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != Corridors.Count ||
                Dungeons.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != Dungeons.Count)
                throw new ArgumentException("Map geometry IDs must be unique within each collection.");
            foreach (var room in Rooms)
                if (room.X < 0 || room.Y < 0 || room.X + room.Width > Width || room.Y + room.Height > Height)
                    throw new ArgumentException($"Room '{room.Id}' is outside the map.");
            foreach (var corridor in Corridors)
            {
                var from = Rooms.SingleOrDefault(r => r.Id == corridor.FromRoomId);
                var to = Rooms.SingleOrDefault(r => r.Id == corridor.ToRoomId);
                if (from == null || to == null || !from.Contains(corridor.Start) || !to.Contains(corridor.End))
                    throw new ArgumentException($"Corridor '{corridor.Id}' must connect its declared rooms.");
            }
        }

        public MapDungeon? FindDungeon(string dungeonId)
        {
            return Dungeons.FirstOrDefault(b => string.Equals(b.Id, dungeonId, StringComparison.Ordinal));
        }

        public MapRoomDefinition? FindRoom(string roomId)
        {
            return Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.Ordinal));
        }

        public MapRoomDefinition? FindEntranceRoom()
        {
            return Rooms.FirstOrDefault(r => r.IsEntrance) ?? Rooms.FirstOrDefault();
        }

        public MapRoomDefinition? FindExitRoom()
        {
            return Rooms.FirstOrDefault(r => r.IsExit) ?? Rooms.FirstOrDefault(r => r.IsEntrance);
        }

    }
}
