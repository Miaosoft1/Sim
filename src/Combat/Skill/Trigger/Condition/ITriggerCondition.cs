namespace Miaosoft.Sim.Combat.Skill;

public interface ITriggerCondition
{
    bool? Evaluate(TriggerConditionContext context);
}
