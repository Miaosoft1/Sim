using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Server.Observation;

public static class ProjectileSnapshotBuilder
{
    public static ProjectileSnapshot Capture(ProjectileFlightState flight)
    {
        ArgumentNullException.ThrowIfNull(flight);
        var movement = flight.Movement;
        return new()
        {
            Id = flight.Launch.ProjectileId,
            MapInstanceId = movement.Body.MapRuntime?.InstanceId ??
                throw new InvalidOperationException("An observed flight requires a live map instance."),
            SourceUnitId = flight.Launch.Source.Id, TargetUnitId = flight.Launch.Target?.Id,
            AbilityId = flight.Launch.AbilityId, AbilityInstanceId = flight.Launch.AbilitySnapshot?.RuntimeInstanceId,
            Position = new(movement.Body.Position.X, movement.Body.Position.Y),
            Destination = new(movement.Destination.X, movement.Destination.Y),
            Speed = movement.Speed, Mode = movement.Mode.ToString()
        };
    }
}
