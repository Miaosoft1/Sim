using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Handler;

namespace Miaosoft.Sim.Server.Control;

internal static class BattleCommandAccess
{
    internal static bool IsCommand(Unit unit, AbilityBase ability) => ability is ICommandAbility &&
        unit.EventReceiver.GetHandlers<TriggerHandler>().Any(trigger => trigger.TriggerType == TriggerType.Command &&
            trigger.TargetInstanceId == ability.RuntimeInstanceId && trigger.TargetAbilityId == ability.Id);
}
