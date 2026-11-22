using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.Tests;

internal sealed class TestCommandAbility : AbilityBase, ICommandAbility
{
    public CommandGateManager? GateManager => null;
}
