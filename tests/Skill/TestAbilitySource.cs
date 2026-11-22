using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.Tests;

internal sealed record TestAbilitySource(Guid InstanceId, string Category = "Test") : IAbilitySource;
