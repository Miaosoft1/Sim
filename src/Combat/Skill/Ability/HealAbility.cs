using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Ability;

public class HealAbility : AbilityBase, ICommandAbility
{
    public CommandGateManager? GateManager { get; set; }
    public TimeSpan? Windup { get; set; }
    public ITargetSelector? Selector { get; set; }
    public int BaseHeal { get; set; }
    public bool RestoreToMaximum { get; set; }

    [Handler]
    public IEnumerable<Event> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
    {
        if (!string.Equals(ev.AbilityId, Id, StringComparison.Ordinal) || ev.Source == null)
            yield break;

        Guid activationId = Guid.NewGuid();
        CommandExecutionFailureReason failure = Prepare(ev, out IReadOnlyList<Unit> targets);
        if (failure != CommandExecutionFailureReason.None)
        {
            yield return CreateResult(ev, activationId, CommandExecutionStatus.Failure, failure);
            yield break;
        }

        if (GateManager is { } gate)
        {
            if (Binding.SourceInstanceId != null) gate.BindAuthority(Binding);
            yield return TriggeredAbilityGate.Request(this, ev, activationId, gate, Windup);
            yield break;
        }

        if (ev.DirectCommand != null)
            yield return CreateResult(ev, activationId, CommandExecutionStatus.Success);
        foreach (Event effect in CreateEffects(ev, targets))
            yield return effect;
    }

    [Handler]
    public IEnumerable<Event> OnActionGateResult(UnitActionGateResultEvent ev)
    {
        UnitActionGateRequestEvent request = ev.Request;
        if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal) ||
            request.Origin is not AbilityTriggerInvokeEvent trigger)
            yield break;

        if (!ev.Result.CanExecute)
            yield return CreateResult(trigger, request.ActivationId, CommandExecutionStatus.Failure,
                ev.Result.FailureReason, ev.Result.NextAvailableTime);
        else
            yield return TriggeredAbilityGate.Release(ev);
    }

    [Handler]
    public IEnumerable<Event> OnTriggeredRelease(TriggeredAbilityReleaseEvent ev)
    {
        UnitActionGateRequestEvent request = ev.Request;
        if (!string.Equals(request.AbilityId, Id, StringComparison.Ordinal) ||
            request.Origin is not AbilityTriggerInvokeEvent trigger)
            yield break;

        CommandGateManager gate = request.AbilityGate
            ?? throw new InvalidOperationException("A gated heal release requires its ability gate.");
        if (trigger.CancellationToken.IsCancellationRequested || request.ActionCancellation.IsCancellationRequested)
        {
            gate.CancelReservation(request.ActivationId);
            yield return TriggeredAbilityGate.Finish(ev, request);
            yield break;
        }

        CommandExecutionFailureReason failure = Prepare(trigger, out IReadOnlyList<Unit> targets);
        if (failure != CommandExecutionFailureReason.None)
        {
            gate.CancelReservation(request.ActivationId);
            yield return CreateResult(trigger, request.ActivationId, CommandExecutionStatus.Failure, failure);
            yield return TriggeredAbilityGate.Finish(ev, request);
            yield break;
        }

        Event[] effects;
        try
        {
            effects = CreateEffects(trigger, targets).ToArray();
        }
        catch
        {
            gate.CancelReservation(request.ActivationId);
            throw;
        }

        CommandReleaseResult result = gate.CommitRelease(request.Source, request.ActivationId,
            ev.Context.ContextTimeSpan, request.ActionCancellation.Token, trigger.CancellationToken, ev);
        if (result != CommandReleaseResult.Committed)
        {
            if (result != CommandReleaseResult.Canceled)
                yield return CreateResult(trigger, request.ActivationId, CommandExecutionStatus.Failure,
                    TriggeredAbilityGate.Failure(result));
            yield return TriggeredAbilityGate.Finish(ev, request);
            yield break;
        }

        if (gate.ResourceCost > 0) yield return UnitEnergyChangedEvent.FromUnit(ev.Context, request.Source);
        yield return CreateResult(trigger, request.ActivationId, CommandExecutionStatus.Success);
        foreach (Event effect in effects) yield return effect;
        foreach (Event next in TriggeredAbilityGate.FinishAfterRelease(ev, request)) yield return next;
    }

    private CommandExecutionFailureReason Prepare(AbilityTriggerInvokeEvent ev, out IReadOnlyList<Unit> targets)
    {
        Unit source = ev.Source ?? throw new InvalidOperationException("A triggered heal requires a source.");
        targets = Array.Empty<Unit>();
        if (!ReferenceEquals(source.EventReceiver.Context, ev.Context) ||
            !ev.Context.ContainsReceiver(source.EventReceiver))
            throw new InvalidOperationException("The healing source must belong to the same context.");
        if (!source.CanParticipate || source.MapObject is { CanCast: false })
            return CommandExecutionFailureReason.SourceNotExist;
        if (Binding.SourceInstanceId != null && !Binding.IsAuthorized(source.Id))
            return CommandExecutionFailureReason.AbilityNotAvailable;

        AbilityTargetingContext targeting = AbilityTargetingContext.FromTrigger(ev);
        if (ev.DirectCommand is { } command)
        {
            if (command.ControlVersion.HasValue && command.ControlVersion != source.MapObjects.ControlVersion)
                return CommandExecutionFailureReason.SourceNotExist;
            if (!CommandTargetVisibility.IsVisible(command, targeting))
                return CommandExecutionFailureReason.TargetNotExist;
            if (command.Target != null && targeting.SourceObject != null &&
                (targeting.TargetObject is not { IsActive: true, IsInteractable: true, IsSelectable: true } body ||
                    !ReferenceEquals(targeting.SourceObject.MapRuntime, body.MapRuntime)))
                return CommandExecutionFailureReason.SelectorRejected;
        }

        IReadOnlyList<Unit> selected;
        if (Selector != null)
        {
            if (source.Registry == null || !Selector.Validate(targeting))
                return CommandExecutionFailureReason.SelectorRejected;
            selected = Selector.ResolveTargets(targeting, source.Registry);
        }
        else
            selected = [ev.Target ?? source];

        if (selected.Any(target => !ReferenceEquals(target.EventReceiver.Context, ev.Context) ||
            !ev.Context.ContainsReceiver(target.EventReceiver)))
            throw new InvalidOperationException("Triggered heal targets must belong to the same context.");
        targets = selected.Where(target => target.CanParticipate).ToArray();
        return targets.Count == 0 ? CommandExecutionFailureReason.TargetNotExist : CommandExecutionFailureReason.None;
    }

    private IEnumerable<Event> CreateEffects(AbilityTriggerInvokeEvent ev, IReadOnlyList<Unit> targets)
    {
        HealProfile profile = HealProfile.From(this);
        foreach (Unit target in targets)
            foreach (Event effect in HealFlow.Create(ev, profile, ev.Source, target))
                yield return effect with { IsScopeDetached = true };
    }

    private Event CreateResult(AbilityTriggerInvokeEvent trigger, Guid activationId,
        CommandExecutionStatus status, CommandExecutionFailureReason failure = CommandExecutionFailureReason.None,
        TimeSpan? nextAvailableTime = null)
    {
        if (trigger.DirectCommand is not { } command)
            return TriggeredAbilityGate.Result(this, trigger, activationId, status, failure, nextAvailableTime);

        return trigger.Context.Factory.NewEvent<CommandExecutionEvent>() with
        {
            Command = command,
            HandlerId = Id,
            Status = status,
            FailureReason = failure,
            NextAvailableTime = nextAvailableTime,
            Receiver = command.Receiver,
            CancellationTokenSource = command.CancellationTokenSource
        };
    }
}
