using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Server.EventData;
using Miaosoft.Sim.Server.Control;
using Miaosoft.Sim.Runtime.EventData;

namespace Miaosoft.Sim.Server.Handler;

internal sealed class BattleSessionCommandHandler(BattleSessionSimulation simulation) : IHandler
{
    public string Id => nameof(BattleSessionCommandHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<IEvent> OnCompletion(SessionCompletionRequestedEvent ev)
    {
        if (simulation.IsCompleted) yield break;
        if (ev.Context.HasEventsDueNowExcept<SessionCompletionRequestedEvent>())
        {
            yield return ev.Context.Factory.NewEvent<SessionCompletionRequestedEvent>() with
            {
                Receiver = ev.Receiver, IsScopeDetached = true
            };
            yield break;
        }
        yield return new SessionCompletedEvent { Create = ev.Context.ContextTimeSpan, Priority = int.MaxValue };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnStarted(BattleSessionStartedEvent ev)
    {
        ev.Context.TimeScale = 1;
        yield break;
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnAdmission(MessageAdmissionEvent ev)
    {
        _ = simulation.Operation(ev.CorrelationId);
        ev.Accepted = true;
        yield break;
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnCommand(MessageEvent<IEventMessage> ev)
    {
        if (ev is MessageAdmissionEvent) yield break;
        SessionInput operation = simulation.Operation(ev.CorrelationId);
        SessionInputAssessment assessment = simulation.AssessInput(operation.UnitId, ev.Message);
        if (!assessment.Allowed)
        {
            yield return simulation.Result(operation, false, assessment.Reason);
            yield break;
        }
        var owner = simulation.Owner(operation);
        if (ev.Message is not SpatialCommand spatial || !owner.CanParticipate ||
            spatial.MapInstanceId != owner.MapObject?.MapRuntime?.InstanceId ||
            spatial.ControlVersion != owner.MapObjects.ControlVersion)
        {
            yield return simulation.Result(operation, false, "The unit or controlled representation changed.");
            yield break;
        }
        switch (ev.Message)
        {
            case MoveCommand move:
                yield return ev.Context.Factory.NewEvent<UnitMoveRequestEvent>() with
                {
                    Source = owner, Destination = new(move.Destination.X, move.Destination.Y),
                    ControlVersion = move.ControlVersion, Receiver = owner.EventReceiver
                };
                break;
            case StopMoveCommand stop:
                yield return ev.Context.Factory.NewEvent<UnitMoveStopEvent>() with
                {
                    Source = owner, ControlVersion = stop.ControlVersion, Receiver = owner.EventReceiver
                };
                break;
            case CastAbilityCommand cast:
                AbilityBase? ability = owner.EventReceiver.GetHandlers<AbilityBase>().SingleOrDefault(candidate =>
                    candidate.RuntimeInstanceId == cast.AbilityInstanceId && BattleCommandAccess.IsCommand(owner, candidate));
                if (ability == null)
                {
                    yield return simulation.Result(operation, false, "The original ability instance is no longer available.");
                    yield break;
                }
                var target = simulation.Target(cast.TargetUnitId);
                var targetObject = owner.MapObject?.MapRuntime is { } map ? target?.MapObjects.Find(map) : null;
                yield return ev.Context.Factory.NewEvent<CommandEvent>() with
                {
                    CommandInstanceId = operation.ExecutionId, AbilityId = ability.Id,
                    AbilityInstanceId = cast.AbilityInstanceId, Source = owner, Target = target,
                    SourceObject = owner.MapObject, TargetObject = targetObject, ControlVersion = cast.ControlVersion,
                    TargetPoint = cast.TargetPoint is { } point ? new(point.X, point.Y) : null,
                    Receiver = owner.EventReceiver
                };
                break;
            default:
                yield return simulation.Result(operation, false, "The selected command has no runtime adapter.");
                break;
        }
    }
}
