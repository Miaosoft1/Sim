using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.Content.Model;

internal sealed record AbilityChargeConditionalRecoveryDefinition(int Amount, AbilityTriggerDefinition Trigger);

internal sealed record AbilityGateDefinition(
    TimeSpan? Windup, TimeSpan? Cooldown, int? ResourceCost, bool OccupiesWindup,
    AbilityChargeConfiguration? Charges, IReadOnlyList<AbilityChargeConditionalRecoveryDefinition> ConditionalRecovery,
    CastingBehavior? Casting)
{
    public CommandGateManager Create() => new()
    {
        Windup = Windup, Cooldown = Cooldown, ResourceCost = ResourceCost,
        OccupiesWindup = OccupiesWindup, Charges = Charges
    };
}
