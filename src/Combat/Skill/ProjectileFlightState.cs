using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class ProjectileFlightState
{
    public required ProjectileEvent Launch { get; init; }
    public required MovementState Movement { get; init; }
    public List<(Unit Unit, Vector2D Position, double Distance, Vector2D TargetPosition, int Order)> Candidates { get; } = [];
    public HashSet<Guid> HitUnits { get; } = [];
    public Dictionary<Guid, MapObject?> TargetObjects { get; } = [];
    public double Travelled { get; set; }
}
