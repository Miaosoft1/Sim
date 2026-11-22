using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Ability;

internal static class TriggeredAbilityGate
{
    public static UnitActionGateRequestEvent Request(
        AbilityBase ability, AbilityTriggerInvokeEvent trigger, Guid activationId,
        CommandGateManager gate, TimeSpan? windup)
    {
        Unit source = trigger.Source ?? throw new InvalidOperationException("A gated trigger requires a source.");
        return trigger.Context.Factory.NewEvent<UnitActionGateRequestEvent>() with
        {
            Source = source, Origin = trigger, CommandId = trigger.CommandEventId,
            ActivationId = activationId, AbilityId = ability.Id, AbilityGate = gate,
            Windup = windup, CheckRestrictions = true, OccupiesChannel = gate.OccupiesWindup,
            Casting = ability.Casting, TriggerChain = trigger.TriggerChain,
            AbilityTarget = source.EventReceiver.CaptureTarget(ability),
            ActionCancellation = new CancellationTokenSource(), Receiver = source.EventReceiver,
            CancellationTokenSource = trigger.CancellationTokenSource
        };
    }

    public static TriggeredAbilityReleaseEvent Release(UnitActionGateResultEvent ev) =>
        ev.Context.Factory.NewEvent<TriggeredAbilityReleaseEvent>() with
        {
            Request = ev.Request, Receiver = ev.Request.Source.EventReceiver,
            TimeSpan = ev.Result.Windup, CancellationTokenSource = ev.Request.ActionCancellation
        };

    public static AbilityActivationResultEvent Result(
        AbilityBase ability, AbilityTriggerInvokeEvent trigger, Guid activationId, CommandExecutionStatus status,
        CommandExecutionFailureReason failure = CommandExecutionFailureReason.None, TimeSpan? nextAvailableTime = null) =>
        trigger.Context.Factory.NewEvent<AbilityActivationResultEvent>() with
        {
            Source = trigger.Source!, AbilityId = ability.Id, AbilityInstanceId = ability.RuntimeInstanceId,
            ActivationId = activationId, CommandEventId = trigger.CommandEventId, Status = status,
            FailureReason = failure, NextAvailableTime = nextAvailableTime,
            Receiver = trigger.Source!.EventReceiver, IsScopeDetached = true
        };

    public static CastFinishedEvent Finish(Event ev, UnitActionGateRequestEvent request) =>
        ev.Context.Factory.NewEvent<CastFinishedEvent>() with
        {
            Source = request.Source, CommandId = request.CommandId, ActivationId = request.ActivationId,
            Receiver = request.Source.EventReceiver
        };

    public static IEnumerable<Event> FinishAfterRelease(Event ev, UnitActionGateRequestEvent request)
    {
        if (request.Casting is { } casting && casting.ChannelDuration > TimeSpan.Zero)
        {
            yield return ev.Context.Factory.NewEvent<CastChannelStartedEvent>() with
            {
                Request = request, Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
            };
            yield return ev.Context.Factory.NewEvent<CastFinishedEvent>() with
            {
                Source = request.Source, CommandId = request.CommandId, ActivationId = request.ActivationId,
                Receiver = ev.Receiver, TimeSpan = casting.ChannelDuration,
                CancellationTokenSource = request.ActionCancellation
            };
        }
        else
            yield return Finish(ev, request);
    }

    public static CommandExecutionFailureReason Failure(CommandReleaseResult result) => result switch
    {
        CommandReleaseResult.ResourceInsufficient => CommandExecutionFailureReason.ResourceInsufficient,
        CommandReleaseResult.AbilityNotAvailable => CommandExecutionFailureReason.AbilityNotAvailable,
        CommandReleaseResult.Casting => CommandExecutionFailureReason.Casting,
        CommandReleaseResult.CoolingDown => CommandExecutionFailureReason.CoolingDown,
        CommandReleaseResult.ChargesDepleted => CommandExecutionFailureReason.ChargesDepleted,
        _ => throw new InvalidOperationException("Unexpected release result.")
    };
}
