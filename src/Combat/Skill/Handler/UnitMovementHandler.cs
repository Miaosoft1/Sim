using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.EventData;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class UnitMovementHandler : Handler, IDisposable
{
    private MovementState? movement;
    private EventScope? movementScope;
    private GridMovement? gridMovement;
    private Unit? owner;
    public UnitMovementHandler() { Id = nameof(UnitMovementHandler); }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRequest(UnitMoveRequestEvent ev)
    {
        if (ev is UnitMoveAuthorizedEvent || ev.Source.CanParticipate) yield break;
        yield return Failure(ev, ev.Source, ev.Source.IsRetired ? "角色已退场，无法移动。" : "角色已阵亡，无法移动。");
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnMove(UnitMoveAuthorizedEvent ev)
    {
        if (ev.ControlVersion.HasValue && ev.ControlVersion != ev.Source.MapObjects.ControlVersion)
        {
            yield return Failure(ev, ev.Source, "地图控制已变更，移动请求已失效。");
            yield break;
        }
        double speed = ev.Source.Property.MoveSpeed ?? ev.Source.Property.MoveSpeedBase ?? 0;
        if (!ev.Source.CanParticipate || ev.Source.MapObject?.CanMove != true ||
            !double.IsFinite(ev.Destination.X) || !double.IsFinite(ev.Destination.Y) ||
            !double.IsFinite(speed) || speed <= 0)
        {
            yield return Failure(ev, ev.Source, "无法移动：角色、坐标或移动速度无效。");
            yield break;
        }

        var body = ev.Source.MapObject;
        var map = body.MapRuntime;
        if (map?.UnitGrid is { } grid)
        {
            double cellsPerSecond = grid.ToCellsPerSecond(speed);
            if (!double.IsFinite(cellsPerSecond) || cellsPerSecond <= 0)
            {
                yield return Failure(ev, ev.Source, "无法移动：棋盘移动速度无效。");
                yield break;
            }
            foreach (Event gridEvent in MoveOnGrid(ev, body, grid, cellsPerSecond)) yield return gridEvent;
            yield break;
        }

        foreach (Event stopped in StopGrid(ev, MovementEndReason.MapChanged)) yield return stopped;
        if (movement is { EndReason: null } active && ReferenceEquals(active.Body, body) &&
            ReferenceEquals(movementScope, ev.Scope))
        {
            // Keep the pending step: repeated input must not postpone it indefinitely.
            var from = active.Body.Position;
            active.Advance(ev.Context.ContextTimeSpan);
            yield return Update(ev, active, from);
            if (!active.EndReason.HasValue)
            {
                active.Retarget(ev.Destination, speed);
                yield return Success(ev, ev.Source, "移动目的地已更新。");
                yield break;
            }
        }

        if (movement is { EndReason: null } previous)
        {
            var from = previous.Body.Position;
            previous.Advance(ev.Context.ContextTimeSpan);
            previous.Stop(MovementEndReason.Stopped);
            yield return Update(ev, previous, from);
        }
        var next = MapMovementEvent.Schedule(ev.Context, ev.Source.EventReceiver, body,
            ev.Destination, speed, isTorus: map?.Definition?.IsTorus == true,
            mapWidth: map?.Width ?? 0, mapHeight: map?.Height ?? 0, stopAtBoundary: true);
        movement = next.Movement;
        movementScope = ev.Scope;
        owner = ev.Source;
        yield return next;
        yield return Success(ev, ev.Source, "移动目的地已更新。");
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnGridStep(UnitGridStepEvent ev)
    {
        GridMovement? active = gridMovement;
        if (active == null || active.Id != ev.MovementId) yield break;
        if (!IsCurrent(active))
        {
            foreach (Event stopped in StopGrid(ev, MovementEndReason.MapChanged)) yield return stopped;
            yield break;
        }
        Vector2D from = active.Body.Position;
        if (!active.Grid.TryCommit(active.Body))
        {
            foreach (Event stopped in StopGrid(ev, MovementEndReason.Blocked)) yield return stopped;
            yield return Failure(ev, active.Owner, "移动被其他棋子阻止，目标不可达。");
            yield break;
        }
        if (active.Body.Position == active.Goal)
        {
            yield return GridUpdate(ev, active, from, active.Body.Position, MovementEndReason.Arrived);
            gridMovement = null;
            yield break;
        }
        yield return GridUpdate(ev, active, from, active.Body.Position, null);
        foreach (Event next in StartGridStep(ev, active)) yield return next;
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnUpdated(MapMovementUpdatedEvent ev)
    {
        if (ev.EndReason == MovementEndReason.Blocked && ev.Movement == movement && owner != null)
            yield return Failure(ev, owner, "移动被边界阻止，目标不可达。");
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnStop(UnitMoveStopEvent ev)
    {
        if (ev.ControlVersion.HasValue && ev.ControlVersion != ev.Source.MapObjects.ControlVersion)
        {
            yield return Failure(ev, ev.Source, "地图控制已变更，停止请求已失效。");
            yield break;
        }
        foreach (Event stopped in Stop(ev, MovementEndReason.Stopped)) yield return stopped;
        yield return Success(ev, ev.Source, "移动已停止。");
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnDeath(DeathResolvedEvent ev) =>
        ev.Victim.EventReceiver == ev.Receiver ? Stop(ev, MovementEndReason.Stopped) : [];

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRetired(UnitRetiredEvent ev) =>
        ev.Unit.EventReceiver == ev.Receiver ? Stop(ev, MovementEndReason.Stopped) : [];

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnCombatScopeStopped(CombatScopeStoppedEvent ev)
    {
        if (ev.Receiver != ev.Unit.EventReceiver) yield break;
        if (gridMovement is { } grid && ReferenceEquals(grid.Scope, ev.StoppedScope))
            foreach (Event stopped in StopGrid(ev, MovementEndReason.Stopped)) yield return stopped;
        if (movement is { EndReason: null } active && ReferenceEquals(movementScope, ev.StoppedScope))
        {
            Vector2D from = active.Body.Position;
            active.Advance(ev.Context.ContextTimeSpan);
            active.Stop(MovementEndReason.Stopped);
            movement = null;
            movementScope = null;
            yield return Update(ev, active, from);
        }
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnMapControlChanged(UnitMapControlChangedEvent ev) =>
        ev.Unit.EventReceiver == ev.Receiver ? Stop(ev, MovementEndReason.MapChanged) : [];

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnInstalled(AbilityInstalledEvent ev) => Refresh(ev, ev.Target);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnUninstalled(AbilityUninstalledEvent ev) => Refresh(ev, ev.Target);

    private IEnumerable<Event> Refresh(Event ev, Unit unit)
    {
        if (gridMovement is { } grid && ev.Receiver == unit.EventReceiver)
        {
            if (!IsCurrent(grid))
            {
                foreach (Event stopped in StopGrid(ev, MovementEndReason.MapChanged)) yield return stopped;
                yield break;
            }
            double nextSpeed = grid.Grid.ToCellsPerSecond(unit.Property.MoveSpeed ?? unit.Property.MoveSpeedBase ?? 0);
            if (nextSpeed != grid.Speed)
            {
                TimeSpan now = ev.Context.ContextTimeSpan;
                grid.Remaining = Math.Max(0, grid.Remaining - grid.Speed * (now - grid.LastAt).TotalSeconds);
                grid.LastAt = now;
                grid.Cancellation?.Cancel();
                if (!double.IsFinite(nextSpeed) || nextSpeed <= 0)
                {
                    foreach (Event stopped in StopGrid(ev, MovementEndReason.Stopped)) yield return stopped;
                }
                else
                {
                    grid.Speed = nextSpeed;
                    foreach (Event next in ScheduleGridStep(ev, grid)) yield return next;
                }
            }
        }
        if (movement is not { EndReason: null } active || ev.Receiver != unit.EventReceiver) yield break;
        double speed = unit.Property.MoveSpeed ?? unit.Property.MoveSpeedBase ?? 0;
        var from = active.Body.Position;
        active.Advance(ev.Context.ContextTimeSpan);
        if (!double.IsFinite(speed) || speed <= 0) active.Stop(MovementEndReason.Stopped);
        else active.Speed = speed;
        yield return Update(ev, active, from);
    }

    private IEnumerable<Event> Stop(Event ev, MovementEndReason reason)
    {
        foreach (Event stopped in StopGrid(ev, reason)) yield return stopped;
        if (movement is not { EndReason: null } active) yield break;
        var from = active.Body.Position;
        active.Advance(ev.Context.ContextTimeSpan);
        active.Stop(reason);
        yield return Update(ev, active, from);
        movement = null;
        movementScope = null;
    }

    private IEnumerable<Event> MoveOnGrid(UnitMoveAuthorizedEvent ev, MapObject body, IMapUnitGrid grid, double speed)
    {
        if (!grid.TryFindPath(body, ev.Destination, out IReadOnlyList<Vector2D> path))
        {
            yield return Failure(ev, ev.Source, "无法移动：目标格不可达或已被占用。");
            yield break;
        }
        if (gridMovement is { } existing && ReferenceEquals(existing.Body, body) &&
            ReferenceEquals(existing.Scope, ev.Scope) && IsCurrent(existing))
        {
            if (path.Count == 0)
            {
                foreach (Event stopped in StopGrid(ev, MovementEndReason.Stopped)) yield return stopped;
            }
            else
            {
                existing.Goal = path[^1];
            }
        }
        else
        {
            foreach (Event stopped in StopGrid(ev, MovementEndReason.MapChanged)) yield return stopped;
            if (path.Count > 0)
            {
                var active = new GridMovement(ev.Source, body, grid, path[^1], speed, ev.Scope);
                gridMovement = active;
                foreach (Event next in StartGridStep(ev, active)) yield return next;
                if (!ReferenceEquals(gridMovement, active)) yield break;
            }
        }
        yield return Success(ev, ev.Source, "移动目的地已更新。");
    }

    private IEnumerable<Event> StartGridStep(Event ev, GridMovement active)
    {
        if (!active.Grid.TryFindPath(active.Body, active.Goal, out IReadOnlyList<Vector2D> path) ||
            path.Count == 0 || !active.Grid.TryReserve(active.Body, path[0]))
        {
            foreach (Event stopped in StopGrid(ev, MovementEndReason.Blocked)) yield return stopped;
            yield return Failure(ev, active.Owner, "移动被其他棋子阻止，目标不可达。");
            yield break;
        }
        active.From = active.Body.Position;
        active.Destination = path[0];
        active.Remaining = 1;
        active.LastAt = ev.Context.ContextTimeSpan;
        foreach (Event next in ScheduleGridStep(ev, active)) yield return next;
    }

    private IEnumerable<Event> ScheduleGridStep(Event ev, GridMovement active)
    {
        double ticks = Math.Round(active.Remaining / active.Speed * TimeSpan.TicksPerSecond);
        long available = TimeSpan.MaxValue.Ticks - ev.Context.ContextTimeSpan.Ticks;
        if (!double.IsFinite(ticks) || ticks > available)
        {
            foreach (Event stopped in StopGrid(ev, MovementEndReason.Stopped)) yield return stopped;
            yield return Failure(ev, active.Owner, "移动速度过低，无法安排下一步。");
            yield break;
        }
        active.Cancellation = new CancellationTokenSource();
        yield return GridUpdate(ev, active, active.Body.Position, active.Destination, null);
        yield return ev.Context.Factory.NewEvent<UnitGridStepEvent>() with
        {
            MovementId = active.Id,
            Receiver = active.Owner.EventReceiver,
            TimeSpan = TimeSpan.FromTicks(Math.Max(0, (long)ticks)),
            CancellationTokenSource = active.Cancellation,
            Scope = active.Scope
        };
    }

    private IEnumerable<Event> StopGrid(Event ev, MovementEndReason reason)
    {
        if (gridMovement is not { } active) yield break;
        active.Cancellation?.Cancel();
        active.Grid.Cancel(active.Body);
        gridMovement = null;
        if (ReferenceEquals(active.Owner.MapObject, active.Body))
            yield return GridUpdate(ev, active, active.Body.Position, active.Body.Position, reason);
    }

    private static bool IsCurrent(GridMovement active) =>
        active.Owner.CanParticipate && active.Body.IsActive && active.Body.CanMove &&
        ReferenceEquals(active.Owner.MapObject, active.Body) &&
        ReferenceEquals(active.Body.MapRuntime?.UnitGrid, active.Grid) &&
        active.Owner.MapObjects.ControlVersion == active.ControlVersion;

    public void Dispose()
    {
        if (gridMovement is { } grid)
        {
            grid.Cancellation?.Cancel();
            grid.Grid.Cancel(grid.Body);
            gridMovement = null;
        }
        movement?.Stop(MovementEndReason.Stopped);
        movement = null;
        movementScope = null;
        owner = null;
    }

    private static UnitGridMovementUpdatedEvent GridUpdate(Event ev, GridMovement active,
        Vector2D from, Vector2D destination, MovementEndReason? reason) =>
        ev.Context.Factory.NewEvent<UnitGridMovementUpdatedEvent>() with
        {
            Source = active.Owner, Body = active.Body, MovementId = active.Id,
            From = from, Position = active.Body.Position, Destination = destination,
            Speed = active.Speed * Math.Sqrt(Math.Pow(active.Destination.X - active.From.X, 2) +
                Math.Pow(active.Destination.Y - active.From.Y, 2)),
            At = ev.Context.ContextTimeSpan, EndReason = reason,
            Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero,
            Scope = ev is CombatScopeStoppedEvent ? null : active.Scope,
            IsScopeDetached = ev is CombatScopeStoppedEvent
        };

    private sealed class GridMovement(Unit owner, MapObject body, IMapUnitGrid grid, Vector2D goal,
        double speed, EventScope? scope)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Unit Owner { get; } = owner;
        public MapObject Body { get; } = body;
        public IMapUnitGrid Grid { get; } = grid;
        public EventScope? Scope { get; } = scope;
        public long ControlVersion { get; } = owner.MapObjects.ControlVersion;
        public Vector2D Goal { get; set; } = goal;
        public Vector2D From { get; set; }
        public Vector2D Destination { get; set; }
        public double Speed { get; set; } = speed;
        public double Remaining { get; set; }
        public TimeSpan LastAt { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private static MapMovementUpdatedEvent Update(Event ev, MovementState active,
        Map.Vector.Vector2D from) => ev.Context.Factory.NewEvent<MapMovementUpdatedEvent>() with
    {
        Movement = active, From = from, Position = active.Body.Position,
        At = ev.Context.ContextTimeSpan, EndReason = active.EndReason,
        Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
    };

    private static UnitActionResultEvent Success(Event ev, Unit unit, string message) =>
        ev.Context.Factory.NewEvent<UnitActionResultEvent>() with
        {
            Source = unit, Success = true, Message = message, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
        };

    private static UnitActionResultEvent Failure(Event ev, Unit unit, string message) =>
        ev.Context.Factory.NewEvent<UnitActionResultEvent>() with
        {
            Source = unit, Success = false, Message = message, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
        };
}
