using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Runtime.EventData;
using Miaosoft.Sim.Server.EventData;
using Miaosoft.Sim.Server.Handler;
using Miaosoft.Sim.Server.Observation;
using Miaosoft.Sim.Server.Control;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Protocol.Event;

namespace Miaosoft.Sim.Server;

public sealed class BattleSessionSimulation : ISessionSimulation
{
    private readonly Dictionary<Guid, Unit> units;
    private readonly Func<IReadOnlyDictionary<Guid, Unit>> worldUnits;
    private readonly Func<Unit?, IReadOnlyList<ObservedUnit>> observe;
    private readonly Dictionary<Guid, SessionInput> operations = [];
    private readonly ContextInputProcessor input;
    private readonly EventReceiver receiver;

    public BattleSessionSimulation(
        Context context, IReadOnlyList<Unit> units, Func<Unit?, IReadOnlyList<ObservedUnit>> observe,
        Func<IReadOnlyDictionary<Guid, Unit>>? worldUnits = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(observe);
        Context = context;
        this.observe = observe;
        this.units = units.ToDictionary(unit => unit.Id);
        this.worldUnits = worldUnits ?? (() => this.units);
        if (units.Count == 0 || units.Any(unit => !ReferenceEquals(unit.EventReceiver.Context, context) ||
            !context.ContainsReceiver(unit.EventReceiver)))
            throw new ArgumentException("Battle participants must be initialized in the supplied context.", nameof(units));
        ParticipantUnitIds = Array.AsReadOnly(units.Select(unit => unit.Id).ToArray());
        IReadOnlyDictionary<Guid, Unit> initialWorld = CurrentUnits();
        if (this.units.Any(entry => !initialWorld.TryGetValue(entry.Key, out Unit? known) ||
            !ReferenceEquals(known, entry.Value)))
            throw new ArgumentException("The initial world must contain the original participant units.", nameof(worldUnits));
        receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new BattleSessionCommandHandler(this));
        input = new(context, receiver);
        context.EventProcessed += OnProcessed;
    }

    public Context Context { get; }
    public IReadOnlyList<Guid> ParticipantUnitIds { get; }
    public bool IsCompleted { get; private set; }
    public bool CanAcceptCompletedInput(IEventMessage message) => false;
    public bool CanAcceptCompletedPostedEvent(IEvent ev) => ev switch
    {
        AbilityRealtimeLeaseRecheckEvent lease => CurrentUnits().TryGetValue(lease.Target.Id, out Unit? unit) &&
            ReferenceEquals(unit, lease.Target) && lease.Receiver == unit.EventReceiver,
        AbilitySimulationLeaseCanceledEvent lease => CurrentUnits().TryGetValue(lease.Target.Id, out Unit? unit) &&
            ReferenceEquals(unit, lease.Target) && lease.Receiver == unit.EventReceiver,
        _ => false
    };

    public SessionInputAssessment AssessInput(Guid unitId, IEventMessage message)
    {
        if (!units.TryGetValue(unitId, out Unit? owner) || message is not IUnitCommand command ||
            command.UnitId != unitId || owner.IsRetired ||
            !CurrentUnits().TryGetValue(unitId, out Unit? present) || !ReferenceEquals(owner, present))
            return new(false, OperationQueueStatus.Unauthorized, "The command does not belong to this participant.");
        if (message is not SpatialCommand spatial || spatial.MapInstanceId == Guid.Empty || spatial.ControlVersion < 1)
            return new(false, OperationQueueStatus.Invalid, "A spatial control binding is required.");
        switch (message)
        {
            case MoveCommand move when !double.IsFinite(move.Destination.X) || !double.IsFinite(move.Destination.Y):
                return new(false, OperationQueueStatus.Invalid, "The destination is invalid.");
            case CastAbilityCommand cast:
                AbilityBase? ability = owner.EventReceiver.GetHandlers<AbilityBase>()
                    .SingleOrDefault(candidate => candidate.RuntimeInstanceId == cast.AbilityInstanceId &&
                        BattleCommandAccess.IsCommand(owner, candidate));
                if (ability == null || ability.Binding.SourceInstanceId != null && !ability.Binding.IsAuthorized(owner.Id))
                    return new(false, OperationQueueStatus.Unauthorized, "The ability instance is not authorized.");
                if (cast.TargetUnitId == Guid.Empty ||
                    cast.TargetPoint is { } point && (!double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    return new(false, OperationQueueStatus.Invalid, "The cast target is invalid.");
                if (cast.TargetUnitId is { } target && observe(owner).All(entry => entry.Unit.Id != target))
                    return new(false, OperationQueueStatus.Unauthorized, "The target is not observed.");
                break;
            case not (MoveCommand or StopMoveCommand):
                return new(false, OperationQueueStatus.Invalid, "The command is not supported by this simulation.");
        }
        return new(true, OperationQueueStatus.Enqueued);
    }

    public IEnumerable<IEvent> CreateInputEvents(SessionInput operation)
    {
        operations.Add(operation.ExecutionId, operation);
        return input.CreateEvents([new(operation.ParticipantId.ToString("N"), operation.Message, operation.ExecutionId)]);
    }

    public IEnumerable<IEvent> CreateStartedEvents() =>
        [Context.Factory.NewEvent<BattleSessionStartedEvent>() with { Receiver = receiver, Priority = int.MinValue }];

    public IEnumerable<IEvent> CreateCompletionEvents() =>
        [Context.Factory.NewEvent<SessionCompletionRequestedEvent>() with { Receiver = receiver }];

    public IEnumerable<IEvent> CreateCompletedEvents()
    {
        foreach (Unit unit in CurrentUnits().Values)
        {
            AbilityInstallHandler installer = unit.EventReceiver.Get<AbilityInstallHandler>() ??
                throw new InvalidOperationException("A completed unit lost its current installer.");
            if (installer.HasExpiredRealTimeLeases(Context.TimeProvider))
                yield return Context.Factory.NewEvent<AbilityRealtimeLeaseRecheckEvent>() with
                {
                    Target = unit, Receiver = unit.EventReceiver
                };
            if (installer.HasCanceledSimulationLeases)
                yield return Context.Factory.NewEvent<AbilitySimulationLeasesRecheckEvent>() with
                {
                    Target = unit, Receiver = unit.EventReceiver
                };
        }
    }

    public WorldSnapshot CaptureWorld(Guid? unitId)
    {
        IReadOnlyDictionary<Guid, Unit> current = CurrentUnits();
        Unit? controlled = unitId.HasValue ? units.GetValueOrDefault(unitId.Value) : null;
        IReadOnlyList<ObservedUnit> visible = observe(controlled);
        if (visible.Select(entry => entry.Unit.Id).Distinct().Count() != visible.Count ||
            visible.Any(entry => !current.TryGetValue(entry.Unit.Id, out Unit? known) || !ReferenceEquals(known, entry.Unit) ||
                entry.Representation.EventReceiver != known.EventReceiver || entry.Representation.MapRuntime == null))
            throw new InvalidOperationException("The selected visibility policy returned an invalid unit representation.");
        var visibility = controlled?.VisibilityQuery?.Invoke();
        string? team = controlled?.TeamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Guid[] maps = visible.Select(entry => entry.Representation.MapRuntime!.InstanceId).Distinct().ToArray();
        ProjectileSnapshot[] projectiles = visibility == null || team == null ? [] :
            current.Values.SelectMany(unit => unit.EventReceiver.GetHandlers<BallisticTravelHandler>())
                .SelectMany(handler => handler.Flights)
                .Where(flight => flight.Movement.Body.MapRuntime is { } map && maps.Contains(map.InstanceId) &&
                    visibility.IsVisible(team, flight.Movement.Body))
                .Select(ProjectileSnapshotBuilder.Capture).ToArray();
        return new()
        {
            Time = Context.ContextTimeSpan, TimeScale = Context.TimeScale, IsCompleted = IsCompleted,
            Units = Array.AsReadOnly(visible.Select(entry => BattleSnapshotBuilder.Capture(
                entry, controlled, Context.ContextTimeSpan)).ToArray()),
            Maps = Array.AsReadOnly(visible.Select(entry => entry.Representation.MapRuntime!)
                .DistinctBy(map => map.InstanceId).Select(map => MapSnapshotBuilder.Capture(
                    map, portal => visibility != null && team != null && visibility.IsVisible(team, portal))).ToArray()),
            Projectiles = Array.AsReadOnly(projectiles)
        };
    }

    public IReadOnlyList<ITransientEventMessage> CaptureTransientEvents(IEvent ev, Guid? unitId)
    {
        if (ev is not (ProjectileEmittedEvent or ProjectileHitPublicationEvent or ProjectileExplosionEvent) ||
            unitId == null || !units.TryGetValue(unitId.Value, out Unit? controlled) ||
            controlled.VisibilityQuery?.Invoke() is not { } visibility)
            return [];
        string team = controlled.TeamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProjectileFact? fact = ProjectileFactBuilder.Capture(ev, (map, position) =>
            observe(controlled).Any(entry => ReferenceEquals(entry.Representation.MapRuntime, map)) &&
            visibility.IsPositionVisible(team, map, position));
        return fact == null ? [] : [fact];
    }

    internal SessionInput Operation(Guid? executionId) =>
        executionId.HasValue && operations.TryGetValue(executionId.Value, out SessionInput? operation)
            ? operation : throw new InvalidOperationException("The input message has no session operation.");
    internal Unit Owner(SessionInput operation) => units[operation.UnitId];
    internal Unit? Target(Guid? unitId) => unitId.HasValue ? CurrentUnits().GetValueOrDefault(unitId.Value) : null;

    private IReadOnlyDictionary<Guid, Unit> CurrentUnits()
    {
        IReadOnlyDictionary<Guid, Unit> current = worldUnits();
        if (current == null || current.Any(entry => entry.Value == null || entry.Key != entry.Value.Id ||
            entry.Value.EventReceiver.Context != Context || !Context.ContainsReceiver(entry.Value.EventReceiver) ||
            units.TryGetValue(entry.Key, out Unit? participant) && !ReferenceEquals(participant, entry.Value)))
            throw new InvalidOperationException("The world directory must contain original units initialized in this context.");
        return current;
    }

    internal SessionOperationCompletedEvent Result(SessionInput operation, bool success, string? failure) =>
        Context.Factory.NewEvent<SessionOperationCompletedEvent>() with
        {
            ExecutionId = operation.ExecutionId,
            Status = success ? OperationExecutionStatus.Succeeded : OperationExecutionStatus.Rejected,
            Reason = failure, Receiver = receiver
        };

    private void OnProcessed(IEvent ev)
    {
        if (ev is SessionCompletedEvent) IsCompleted = true;
        if (ev is SessionOperationCompletedEvent outcome) { operations.Remove(outcome.ExecutionId); return; }
        if (ev is CommandExecutionEvent command && operations.TryGetValue(command.Command.CommandInstanceId, out SessionInput? cast) &&
            cast.Message is CastAbilityCommand expected && command.Command.Source.Id == cast.UnitId &&
            command.Command.AbilityInstanceId == expected.AbilityInstanceId && command.HandlerId == command.Command.AbilityId)
            Context.Dispatch(Result(cast, command.Status == CommandExecutionStatus.Success,
                command.Status == CommandExecutionStatus.Success ? null : command.FailureReason.ToString())).ToList();
        else if (ev is UnitActionResultEvent { CorrelationId: { } id } action &&
            operations.TryGetValue(id, out SessionInput? move) && move.Message is MoveCommand or StopMoveCommand)
            Context.Dispatch(Result(move, action.Success, action.Success ? null : action.Message)).ToList();
    }
}
