using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Map
{
    public sealed class MapRoom
    {
        public MapRoom(MapObject dungeon, double interiorWidth = 10, double interiorHeight = 10)
        {
            ArgumentNullException.ThrowIfNull(dungeon);
            if (dungeon.Kind != MapObjectKind.Dungeon)
            {
                throw new ArgumentException("A room must be associated with a dungeon map object.", nameof(dungeon));
            }

            if (interiorWidth <= 0 || !double.IsFinite(interiorWidth))
            {
                throw new ArgumentOutOfRangeException(nameof(interiorWidth));
            }

            if (interiorHeight <= 0 || !double.IsFinite(interiorHeight))
            {
                throw new ArgumentOutOfRangeException(nameof(interiorHeight));
            }

            Dungeon = dungeon;
            InteriorSize = new Vector2D(interiorWidth, interiorHeight);
        }

        public MapObject Dungeon { get; }
        public Vector2D InteriorSize { get; }
    }
}
