using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Content.Tests;

internal sealed record TestAbilitySource(Guid InstanceId, string Category = "Test") : IAbilitySource;
