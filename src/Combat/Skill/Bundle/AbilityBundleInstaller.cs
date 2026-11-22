using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Bundle
{
    /// <summary>
    /// Installs and uninstalls <see cref="AbilityBundle"/> members on a unit by emitting
    /// install/uninstall request events; execution is done by the preset
    /// <see cref="Miaosoft.Sim.Combat.Skill.Handler.AbilityInstallHandler"/>.
    /// This class never mounts handlers or applies ability instances directly.
    /// </summary>
    public static class AbilityBundleInstaller
    {
        public static void Install(Context context, Unit unit, AbilityBundle bundle, int? level = null)
        {
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(),
                Source = unit,
                Target = unit,
                AbilityIds = bundle.AbilityIds,
                Level = level,
                Receiver = unit.EventReceiver,
                Create = context.ContextTimeSpan,
                TimeSpan = TimeSpan.Zero,
            }).ToList();
        }

        public static void Uninstall(Context context, Unit unit, AbilityBundle bundle)
        {
            context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(),
                Source = unit,
                Target = unit,
                AbilityIds = bundle.AbilityIds,
                Receiver = unit.EventReceiver,
                Create = context.ContextTimeSpan,
                TimeSpan = TimeSpan.Zero,
            }).ToList();
        }
    }
}
