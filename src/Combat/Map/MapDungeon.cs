using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapDungeon
    {
        public MapDungeon(
            string id,
            string name,
            Vector2D position,
            Vector2D? size = null,
            Vector2D? entrancePosition = null,
            double interactionRadius = 5.0,
            string? description = null,
            string? targetMapId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
                throw new ArgumentOutOfRangeException(nameof(position));
            if (size is { } dimensions && (!double.IsFinite(dimensions.X) || !double.IsFinite(dimensions.Y) ||
                dimensions.X <= 0 || dimensions.Y <= 0))
                throw new ArgumentOutOfRangeException(nameof(size));
            if (entrancePosition is { } entrance && (!double.IsFinite(entrance.X) || !double.IsFinite(entrance.Y)))
                throw new ArgumentOutOfRangeException(nameof(entrancePosition));
            if (!double.IsFinite(interactionRadius) || interactionRadius <= 0)
                throw new ArgumentOutOfRangeException(nameof(interactionRadius));

            Id = id;
            Name = name;
            Position = position;
            Size = size ?? new Vector2D(10, 10);
            EntrancePosition = entrancePosition ?? position;
            InteractionRadius = interactionRadius;
            Description = description ?? string.Empty;
            TargetMapId = targetMapId;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public Vector2D Position { get; }
        public Vector2D Size { get; }
        public Vector2D EntrancePosition { get; }
        public double InteractionRadius { get; }
        public string? TargetMapId { get; }

        public bool IsWithinInteractionRange(Vector2D playerPosition, MapSpace? space = null)
        {
            space ??= MapSpace.Planar;
            return space.Distance(playerPosition, EntrancePosition) <= InteractionRadius ||
                   space.Distance(playerPosition, Position) <= InteractionRadius;
        }
    }
}
