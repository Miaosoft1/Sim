using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;

namespace Miaosoft.Sim.Server.Observation;

public static class ProjectileFactBuilder
{
    public static ProjectileFact? Capture(IEvent ev, Func<MapRuntime, Vector2D, bool> observe)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(observe);
        return ev switch
        {
            ProjectileEmittedEvent emitted => Build(emitted, emitted.Launch.ProjectileId,
                emitted.Launch.AbilityId, emitted.Launch.AbilitySnapshot?.RuntimeInstanceId,
                emitted.Launch.Source, emitted.Launch.Target, emitted.Map, emitted.Position,
                ProjectileFactKind.Emitted, emitted.Destination, observe),
            ProjectileHitPublicationEvent publication => Hit(publication, publication.Hit, ProjectileFactKind.Hit, observe),
            ProjectileExplosionEvent explosion => Hit(explosion, explosion, ProjectileFactKind.Explosion, observe),
            _ => null
        };
    }

    private static ProjectileFact? Hit(Event publication, ProjectileHitEvent hit, ProjectileFactKind kind,
        Func<MapRuntime, Vector2D, bool> observe) =>
        hit.HitPoint is { } point ? Build(publication, hit.ProjectileId, hit.AbilityId,
            hit.AbilitySnapshot?.RuntimeInstanceId, hit.Source, hit.Target, hit.HitMap, point, kind, null, observe) : null;

    private static ProjectileFact? Build(Event publication, Guid projectileId, string abilityId, Guid? instanceId,
        Unit source, Unit? target, MapRuntime? map, Vector2D position, ProjectileFactKind kind,
        Vector2D? destination, Func<MapRuntime, Vector2D, bool> observe)
    {
        if (map == null || !observe(map, position)) return null;
        return new()
        {
            EventSequence = publication.EnqueueSequence,
            Time = publication.Execute ?? throw new InvalidOperationException("Only a processed projectile fact can be observed."),
            Kind = kind, ProjectileId = projectileId, MapInstanceId = map.InstanceId,
            SourceUnitId = source.Id, TargetUnitId = target?.Id,
            AbilityId = abilityId, AbilityInstanceId = instanceId, Position = new(position.X, position.Y),
            Destination = destination is { } point ? new(point.X, point.Y) : null
        };
    }
}
