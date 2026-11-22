using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Control;

public sealed record UnitCommandAssessment(bool Ready, string? Failure = null);

public interface IUnitCommandPolicy
{
    UnitCommandAssessment? Evaluate(IUnitCommand command, WorldSnapshot world, UnitSnapshot controlled);
}

public static class UnitCommandPreconditions
{
    public static UnitCommandAssessment Evaluate(
        IUnitCommand command, WorldSnapshot world, Guid controlledUnitId, IUnitCommandPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(world);
        UnitCommandAssessment Reject(string reason) => new(false, reason);
        UnitSnapshot? self = world.Units.SingleOrDefault(unit => unit.Id == controlledUnitId);
        if (self == null) return Reject("The controlled unit is unavailable.");
        if (command.UnitId != controlledUnitId) return Reject("The command belongs to another unit.");
        if (!self.IsAlive) return Reject("The controlled unit is dead.");
        if (command is SpatialCommand spatial &&
            (spatial.MapInstanceId != self.MapInstanceId || spatial.ControlVersion != self.ControlVersion))
            return Reject("The controlled representation has changed.");
        UnitCommandAssessment? assessment = policy?.Evaluate(command, world, self);
        if (assessment is { Ready: false }) return assessment;
        switch (command)
        {
            case MoveCommand move:
                if (!Finite(move.Destination)) return Reject("The destination is not finite.");
                if (!self.CanMove || !double.IsFinite(self.MoveSpeed) || self.MoveSpeed <= 0)
                    return Reject("The unit cannot move.");
                return new(true);
            case StopMoveCommand:
                return new(true);
            case CastAbilityCommand cast:
                if (!self.CanCast) return Reject("The controlled representation cannot cast.");
                if (cast.AbilityInstanceId == Guid.Empty || cast.TargetUnitId == Guid.Empty ||
                    cast.TargetPoint is { } point && !Finite(point))
                    return Reject("The ability or target is invalid.");
                AbilitySnapshot? ability = self.Abilities.SingleOrDefault(candidate =>
                    candidate.RuntimeInstanceId == cast.AbilityInstanceId);
                if (ability == null) return Reject("The ability instance is not owned by the unit.");
                if (!double.IsFinite(ability.RemainingCooldownSeconds) || ability.RemainingCooldownSeconds < 0)
                    throw new InvalidDataException("The observed cooldown is invalid.");
                if (!ability.CanActivate || ability.RemainingCooldownSeconds > 0 || ability.AvailableCharges == 0)
                    return Reject("The ability instance is not currently available.");
                if (cast.TargetUnitId is { } id && world.Units.All(unit => unit.Id != id))
                    return Reject("The target is not observed.");
                return new(true);
            default:
                return assessment ?? Reject("The command is not handled by the selected control policy.");
        }
    }

    private static bool Finite(WorldPosition point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
