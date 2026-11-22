using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Handler;

namespace Miaosoft.Sim.Combat.Skill.Tests;

internal static class TestHandlerFactory
{
    public static void Register(Unit unit, params IHandler[] handlers)
    {
        AbilityInstallHandler installer = unit.EventReceiver.Get<AbilityInstallHandler>()
            ?? throw new InvalidOperationException("The unit has no ability installer.");
        Func<string, int?, IReadOnlyList<IHandler>>? previous = installer.AbilityFactory;
        Dictionary<string, IHandler> overrides = handlers.ToDictionary(handler => handler.Id, StringComparer.Ordinal);
        installer.AbilityFactory = (id, level) => overrides.TryGetValue(id, out IHandler? handler)
            ? [handler]
            : previous?.Invoke(id, level)
                ?? throw new InvalidOperationException($"No test ability factory is registered for '{id}'.");
    }
}
