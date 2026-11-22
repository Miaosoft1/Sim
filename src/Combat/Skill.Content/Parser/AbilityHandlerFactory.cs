using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

public static class AbilityHandlerFactory
{
    public static IReadOnlyList<IHandler> WithTrigger(
        AbilityBase ability, AbilityContentDefinition definition) => WithTrigger(ability, definition, null);

    internal static IReadOnlyList<IHandler> WithTrigger(
        AbilityBase ability, AbilityContentDefinition definition, AbilityGateDefinition? gate)
    {
        var handlers = new List<IHandler> { ability };
        if (definition.Trigger.Type != TriggerType.None)
            handlers.Add(CreateTrigger(ability, definition, definition.Trigger, $"{definition.Id}.trigger", null));
        if (gate != null)
            for (int index = 0; index < gate.ConditionalRecovery.Count; index++)
            {
                AbilityChargeConditionalRecoveryDefinition recovery = gate.ConditionalRecovery[index];
                handlers.Add(CreateTrigger(ability, definition, recovery.Trigger,
                    $"{definition.Id}.charge-recovery.{index}", recovery.Amount));
            }
        return handlers;
    }

    public static TriggerHandler CreateTrigger(
        AbilityBase ability, AbilityContentDefinition definition, AbilityTriggerDefinition configured, string id)
    {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!string.Equals(ability.Id, definition.Id, StringComparison.Ordinal))
            throw new ArgumentException("A trigger must bind the ability described by its definition.", nameof(ability));
        return CreateTrigger(ability, definition, configured, id, null);
    }

    private static TriggerHandler CreateTrigger(
        AbilityBase ability, AbilityContentDefinition definition, AbilityTriggerDefinition configured,
        string id, int? restoreAmount)
    {
        var trigger = new TriggerHandler
        {
            Id = id,
            Priority = definition.Priority,
            TargetAbilityId = definition.Id,
            ChargeRestoreAmount = restoreAmount,
            Config = new AbilityTriggerConfig
            {
                Type = configured.Type,
                EventSourceScope = configured.EventSourceScope,
                DeathPhase = configured.DeathPhase,
                SignalId = configured.SignalId,
                SourceAbilityIds = configured.SourceAbilityIds,
                Probability = configured.Probability,
                Conditions = configured.Conditions
            }
        };
        var transaction = new AtomicTransaction();
        trigger.StageBindTarget(transaction, ability.RuntimeInstanceId);
        transaction.Commit();
        return trigger;
    }
}
