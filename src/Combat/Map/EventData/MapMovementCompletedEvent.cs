using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.EventData
{
    public record class MapMovementCompletedEvent : Event
    {
        public required MapObject Target { get; init; }
        public required Vector2D Destination { get; init; }
    }
}
