using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class AbilityChargeRestoreHandler : Handler
{
    public AbilityChargeRestoreHandler() => Id = nameof(AbilityChargeRestoreHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnTriggeredRestore(AbilityChargeRestoreInvokeEvent ev)
    {
        if (ev.Source == null || !ev.TargetRuntimeInstanceId.HasValue ||
            ev.TriggerRegistration == null || ev.AbilityRegistration == null || !ev.AuthorityVersion.HasValue ||
            !ev.TriggerInstanceId.HasValue || !ev.TriggerAuthorityVersion.HasValue ||
            !ReferenceEquals(ev.Receiver, ev.Source.EventReceiver))
            throw new InvalidOperationException("A triggered charge restore requires its owner and ability instance.");
        yield return ev.Context.Factory.NewEvent<AbilityChargeRestoreRequestEvent>() with
        {
            Owner = ev.Source,
            AbilityInstanceId = ev.TargetRuntimeInstanceId.Value,
            Amount = ev.Amount,
            TriggerRegistration = ev.TriggerRegistration,
            AbilityRegistration = ev.AbilityRegistration,
            AuthorityVersion = ev.AuthorityVersion,
            TriggerInstanceId = ev.TriggerInstanceId,
            TriggerAuthorityVersion = ev.TriggerAuthorityVersion,
            Receiver = ev.Receiver,
            CancellationTokenSource = ev.CancellationTokenSource,
            Scope = ev.Scope
        };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRestore(AbilityChargeRestoreRequestEvent ev)
    {
        if (ev.AbilityInstanceId == Guid.Empty || ev.Amount <= 0)
            throw new InvalidOperationException("A charge restore request needs an ability instance and positive amount.");
        if (!ReferenceEquals(ev.Receiver, ev.Owner.EventReceiver) ||
            !ReferenceEquals(ev.Owner.EventReceiver.Context, ev.Context) ||
            !ev.Context.ContainsReceiver(ev.Owner.EventReceiver))
            throw new InvalidOperationException("A charge restore request must target an owner in its context.");

        AbilityBase? ability = ev.Owner.EventReceiver.GetHandlers<AbilityBase>()
            .SingleOrDefault(candidate => candidate.RuntimeInstanceId == ev.AbilityInstanceId);
        TriggerHandler? trigger = ev.TriggerInstanceId is { } triggerId
            ? ev.Owner.EventReceiver.GetHandlers<TriggerHandler>()
                .SingleOrDefault(candidate => candidate.Binding.InstanceId == triggerId)
            : null;
        AbilityChargeRestoreFailureReason reason;
        int restored = 0;
        if (ability == null || ev.TriggerRegistration is { IsCurrent: false } ||
            ev.AbilityRegistration is { IsCurrent: false } ||
            ev.AuthorityVersion is { } version && ability.Binding.AuthorityVersion != version ||
            ev.AbilityRegistration != null && !ev.AbilityRegistration.Targets(ev.Owner.EventReceiver, ability) ||
            ev.TriggerInstanceId.HasValue &&
                (trigger == null || ev.TriggerRegistration == null ||
                 !ev.TriggerRegistration.Targets(ev.Owner.EventReceiver, trigger) ||
                 trigger.Binding.AuthorityVersion != ev.TriggerAuthorityVersion ||
                 !ReferenceEquals(trigger.Binding.Source, ability.Binding.Source) ||
                 trigger.Binding.SourceInstanceId != null && !trigger.Binding.IsAuthorized(ev.Owner.Id)) ||
            ability.Binding.SourceInstanceId != null && !ability.Binding.IsAuthorized(ev.Owner.Id))
            reason = AbilityChargeRestoreFailureReason.AbilityNotAvailable;
        else if (ability is not IAbilityGate { GateManager.Charges: not null } command)
            reason = AbilityChargeRestoreFailureReason.ChargesNotConfigured;
        else
        {
            restored = command.GateManager!.RestoreCharges(ev.Context.ContextTimeSpan, ev.Amount);
            reason = AbilityChargeRestoreFailureReason.None;
        }
        yield return ev.Context.Factory.NewEvent<AbilityChargeRestoreResultEvent>() with
        {
            Owner = ev.Owner,
            AbilityInstanceId = ev.AbilityInstanceId,
            Restored = restored,
            FailureReason = reason,
            Receiver = ev.Receiver,
            IsScopeDetached = true
        };
    }
}
