using System.Globalization;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Targeting;

public static class CommandTargetVisibility
{
    public static bool IsVisible(CommandEvent command, AbilityTargetingContext targeting)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(targeting);
        if (command.Target == null) return true;
        if (!command.Target.CanParticipate) return false;
        var sourceMap = targeting.SourceObject?.MapRuntime;
        if (sourceMap == null && targeting.TargetObject?.MapRuntime == null &&
            command.Source.VisibilityQuery == null)
            return true;
        if (sourceMap == null || targeting.TargetObject is not { } target ||
            !ReferenceEquals(target.MapRuntime, sourceMap))
            return false;
        var query = command.Source.VisibilityQuery ??
            throw new InvalidOperationException("A mapped command source requires a visibility snapshot provider.");
        return query().IsVisible(command.Source.TeamId.ToString(CultureInfo.InvariantCulture), target);
    }
}
