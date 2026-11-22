using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.EventData
{
    public record class ProjectileCollisionEvent : Event
    {
        public required MapObject Projectile { get; init; }
        public required MapObject Target { get; init; }
        public required Vector2D Position { get; init; }
    }
}
