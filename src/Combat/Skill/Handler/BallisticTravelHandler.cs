using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.EventData;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public class BallisticTravelHandler : Handler, IDisposable
{
    private readonly Dictionary<Guid, ProjectileFlightState> flights = [];
    public IReadOnlyCollection<ProjectileFlightState> Flights => flights.Values;

    public BallisticTravelHandler() { Id = nameof(BallisticTravelHandler); Priority = int.MinValue; }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnProjectile(ProjectileEvent ev)
    {
        if (ev.Receiver != ev.Source.EventReceiver || ev.CancellationToken.IsCancellationRequested ||
            ev.Source.IsRetired) yield break;
        if (!Enum.IsDefined(ev.HitResponse) || ev.MaxTargets is <= 0 ||
            (ev.ExplosionTrigger & ~(ProjectileExplosionTrigger.OnHit | ProjectileExplosionTrigger.OnArrival)) != 0)
            throw new ArgumentException($"Projectile '{ev.AbilityId}' has invalid impact settings.");
        var source = ev.SourceObject ?? ev.Source.MapObject;
        var targetBody = ev.TargetObject ?? (ev.Target == null ? null :
            Targeting.UnitSpatialRegistry.ObjectIn(ev.Target, source?.MapRuntime));
        Vector2D origin = source?.Position ?? Vector2D.Zero;
        Vector2D destination = targetBody?.Position ?? ev.TargetPoint ?? origin;
        var body = new MapObject(ev.ProjectileId.ToString("D"), origin, ev.Receiver, kind: MapObjectKind.Projectile)
        {
            MovementInterval = source?.MovementInterval ?? MovementTiming.DefaultInterval
        };
        source?.MapRuntime?.AddObject(body);
        double speed = ev.ProjectileSpeed ?? 0;
        MovementMode mode = speed == 0 ? MovementMode.Instant : ev.Mode;
        if (mode == MovementMode.Homing && targetBody == null)
            throw new InvalidOperationException($"Projectile '{ev.AbilityId}' requires a spatial target.");
        var move = MapMovementEvent.Schedule(ev.Context, ev.Receiver, body, destination, speed,
            mode: mode, trackingTarget: mode == MovementMode.Homing ? targetBody : null,
            deferFirstStep: true);
        var flight = new ProjectileFlightState { Launch = ev with { SourceObject = source, TargetObject = targetBody }, Movement = move.Movement! };
        if (ev.Target != null) flight.TargetObjects[ev.Target.Id] = targetBody;
        if (ev.SelectedTargets != null)
        {
            double dx = destination.X - origin.X, dy = destination.Y - origin.Y;
            double length = origin.DistanceTo(destination);
            foreach (Unit unit in ev.SelectedTargets.DistinctBy(unit => unit.Id))
            {
                var representation = Targeting.UnitSpatialRegistry.ObjectIn(unit, source?.MapRuntime);
                if (representation == null || !representation.IsInteractable) continue;
                flight.TargetObjects[unit.Id] = representation;
                Vector2D point = representation.Position;
                double distance = length == 0 ? 0 : Math.Clamp(
                    ((point.X - origin.X) * dx + (point.Y - origin.Y) * dy) / length, 0, length);
                flight.Candidates.Add((unit, origin.MoveTowards(destination, distance), distance, point, flight.Candidates.Count));
            }
            flight.Candidates.Sort((a, b) =>
            {
                int distance = a.Distance.CompareTo(b.Distance);
                return distance != 0 ? distance : a.Order.CompareTo(b.Order);
            });
            flight.Movement.Checkpoints.AddRange(flight.Candidates.Select(hit => hit.Distance));
        }
        flights.Add(flight.Movement.Id, flight);
        yield return ev.Context.Factory.NewEvent<ProjectileEmittedEvent>() with
        {
            Launch = flight.Launch, Map = body.MapRuntime, Position = origin, Destination = destination,
            Receiver = ev.Receiver
        };
        yield return move;
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnMoved(MapMovementUpdatedEvent ev)
    {
        if (!flights.TryGetValue(ev.Movement.Id, out var flight)) yield break;
        ProjectileEvent launch = flight.Launch;
        if (launch.CancellationToken.IsCancellationRequested || launch.Source.IsRetired)
            ev.Movement.Stop(MovementEndReason.Stopped, replaceEnd: true);
        flight.Travelled += ev.From.DistanceTo(ev.Position);
        bool explodedAtArrival = false;
        if (!launch.CancellationToken.IsCancellationRequested && !launch.Source.IsRetired &&
            launch.HitResponse != ProjectileHitResponse.Ignore && !ev.Movement.TargetLost)
        {
            var hits = flight.Candidates.Where(hit => hit.Distance <= flight.Travelled + 1e-8).ToList();
            if (launch.SelectedTargets == null && ev.EndReason == MovementEndReason.Arrived &&
                launch.Target is { CanParticipate: true } target)
                hits.Add((target, ev.Position, flight.Travelled,
                    flight.TargetObjects.GetValueOrDefault(target.Id)?.Position ?? ev.Position, hits.Count));
            foreach (var hit in hits)
            {
                flight.Candidates.Remove(hit);
                var targetObject = flight.TargetObjects.GetValueOrDefault(hit.Unit.Id);
                if (!hit.Unit.CanParticipate ||
                    targetObject is not { IsActive: true, IsInteractable: true, IsSelectable: true } ||
                    !ReferenceEquals(targetObject.MapRuntime, ev.Movement.Body.MapRuntime) ||
                    launch.SelectedTargets != null && targetObject.MapRuntime?.UnitGrid != null &&
                        targetObject.Position != hit.TargetPosition ||
                    !flight.HitUnits.Add(hit.Unit.Id)) continue;
                foreach (Event result in Hit(ev, launch, hit.Unit, targetObject, hit.Position)) yield return result;
                if (launch.ExplosionTrigger.HasFlag(ProjectileExplosionTrigger.OnHit))
                {
                    yield return Explosion(ev, launch, hit.Position, ev.Movement.Body.MapRuntime);
                    explodedAtArrival |= hit.Position.DistanceTo(ev.Position) < 1e-8 &&
                        ev.EndReason == MovementEndReason.Arrived;
                }
                if (launch.HitResponse == ProjectileHitResponse.Stop ||
                    launch.MaxTargets is int max && flight.HitUnits.Count >= max)
                {
                    bool beforeEnd = hit.Position.DistanceTo(ev.Movement.Destination) > 1e-8;
                    if (beforeEnd) ev.Movement.Body.Position = hit.Position;
                    ev.Movement.Stop(MovementEndReason.Stopped, replaceEnd: beforeEnd);
                    break;
                }
            }
        }
        if (!ev.Movement.EndReason.HasValue) yield break;
        bool arrived = ev.Movement.EndReason is MovementEndReason.Arrived or MovementEndReason.Missed;
        if (arrived && !explodedAtArrival &&
            launch.ExplosionTrigger.HasFlag(ProjectileExplosionTrigger.OnArrival))
            yield return Explosion(ev, launch, ev.Movement.Body.Position, ev.Movement.Body.MapRuntime);
        flights.Remove(ev.Movement.Id);
        var map = ev.Movement.Body.MapRuntime;
        map?.RemoveObject(ev.Movement.Body);
        yield return ev.Context.Factory.NewEvent<ProjectileEndedEvent>() with
        {
            ProjectileId = launch.ProjectileId, Position = ev.Movement.Body.Position, Reason = ev.Movement.EndReason.Value,
            Map = map,
            Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
        };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRetired(UnitRetiredEvent ev)
    {
        if (ev.Receiver != ev.Unit.EventReceiver) yield break;
        foreach (Event ended in StopFlights(ev, flights.Values.ToArray())) yield return ended;
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnCombatScopeStopped(CombatScopeStoppedEvent ev)
    {
        if (ev.Receiver != ev.Unit.EventReceiver) yield break;
        foreach (Event ended in StopFlights(ev, flights.Values
            .Where(flight => ReferenceEquals(flight.Launch.Scope, ev.StoppedScope)).ToArray()))
            yield return ended;
    }

    private IEnumerable<Event> StopFlights(Event ev, IEnumerable<ProjectileFlightState> stopped)
    {
        foreach (ProjectileFlightState flight in stopped)
        {
            flight.Launch.CancellationTokenSource?.Cancel();
            flight.Movement.Stop(MovementEndReason.Stopped, replaceEnd: true);
            flights.Remove(flight.Movement.Id);
            MapRuntime? map = flight.Movement.Body.MapRuntime;
            map?.RemoveObject(flight.Movement.Body);
            yield return ev.Context.Factory.NewEvent<ProjectileEndedEvent>() with
            {
                ProjectileId = flight.Launch.ProjectileId,
                Position = flight.Movement.Body.Position,
                Reason = MovementEndReason.Stopped,
                Map = map,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero
            };
        }
    }

    public void Dispose()
    {
        foreach (ProjectileFlightState flight in flights.Values)
        {
            flight.Launch.CancellationTokenSource?.Cancel();
            flight.Movement.Stop(MovementEndReason.Stopped, replaceEnd: true);
            flight.Movement.Body.MapRuntime?.RemoveObject(flight.Movement.Body);
        }
        flights.Clear();
    }

    private static IEnumerable<Event> Hit(Event ev, ProjectileEvent launch, Unit target, MapObject targetObject, Vector2D point)
    {
        var hit = ev.Context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            ProjectileId = launch.ProjectileId, CommandEventId = launch.CommandEventId,
            AbilityId = launch.AbilityId, AbilitySnapshot = launch.AbilitySnapshot,
            Source = launch.Source, Target = target, HitPoint = point,
            HitMap = targetObject.MapRuntime,
            CompletionReceiver = launch.CompletionReceiver,
            SourceObject = launch.SourceObject, TargetObject = targetObject,
            TriggerChain = launch.TriggerChain, Receiver = launch.Source.EventReceiver, TimeSpan = TimeSpan.Zero
        };
        yield return ProjectileHitPublicationEvent.FromHit(hit);
    }

    private static ProjectileExplosionEvent Explosion(Event ev, ProjectileEvent launch, Vector2D point, MapRuntime? map) =>
        ev.Context.Factory.NewEvent<ProjectileExplosionEvent>() with
        {
            ProjectileId = launch.ProjectileId, CommandEventId = launch.CommandEventId,
            AbilityId = launch.AbilityId, AbilitySnapshot = launch.AbilitySnapshot,
            Source = launch.Source, Target = null, HitPoint = point,
            HitMap = map,
            SourceObject = launch.SourceObject,
            TriggerChain = launch.TriggerChain, Receiver = launch.Source.EventReceiver, TimeSpan = TimeSpan.Zero
        };
}
