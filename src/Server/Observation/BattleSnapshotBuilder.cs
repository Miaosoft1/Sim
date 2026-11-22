using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Server.Control;
using Miaosoft.Sim.Combat.Skill.Handler;

namespace Miaosoft.Sim.Server.Observation;

public static class BattleSnapshotBuilder
{
    public static UnitSnapshot Capture(ObservedUnit observed, Unit? controlled, TimeSpan now)
    {
        Unit unit = observed.Unit;
        var body = observed.Representation;
        var property = unit.Property;
        return new()
        {
            Id = unit.Id, Name = unit.Name, TeamId = unit.TeamId, IsAlive = unit.IsAlive, DeathId = unit.CurrentDeathId,
            Position = new(body.Position.X, body.Position.Y), MapInstanceId = body.MapRuntime?.InstanceId,
            ControlVersion = unit.MapObjects.ControlVersion, CanMove = body.CanMove, CanCast = body.CanCast,
            Life = property.LifePoint ?? property.LifePointMax, MaximumLife = property.LifePointMax,
            Energy = property.Energy, MaximumEnergy = property.EnergyMax, Level = property.Level,
            Experience = ReferenceEquals(unit, controlled) ? property.Experience : null,
            MaximumExperience = ReferenceEquals(unit, controlled) ? property.ExperienceMax : null,
            MoveSpeed = property.MoveSpeed ?? property.MoveSpeedBase ?? 0,
            Statuses = Array.AsReadOnly(unit.Abilities.GetAbilitiesByLabel(AbilityLabel.Status)
                .Select(ability => CaptureStatus(unit, ability)).ToArray()),
            Abilities = ReferenceEquals(unit, controlled)
                ? Array.AsReadOnly(unit.EventReceiver.GetHandlers<AbilityBase>()
                    .Where(ability => BattleCommandAccess.IsCommand(unit, ability))
                    .Select(ability => CaptureAbility(unit, ability, now)).ToArray())
                : []
        };
    }

    private static StatusSnapshot CaptureStatus(Unit unit, AbilityBase ability)
    {
        StatusAbility? status = ability as StatusAbility;
        TimeSpan? expiresAt = null;
        bool realTime = status?.TimeBasis == AbilityTimeBasis.RealTime;
        if (!realTime && status?.ApplicationId != null)
        {
            AbilityInstallHandler installer = unit.EventReceiver.Get<AbilityInstallHandler>() ??
                throw new InvalidOperationException("An applied status requires its current installer.");
            if (!installer.TryGetSimulationLeaseExpiration(status.Id, out expiresAt) && status.Duration > TimeSpan.Zero)
                throw new InvalidOperationException("A timed status lost its committed application lease.");
            if (expiresAt == TimeSpan.MaxValue) expiresAt = null;
        }
        return new()
        {
            RuntimeInstanceId = ability.RuntimeInstanceId, ApplicationId = status?.ApplicationId,
            DefinitionId = status?.PayloadId ?? ability.Id, Name = ability.Name ?? ability.Id,
            SourceUnitId = status?.ApplicationSource?.Id, Stacks = status?.CurrentStack ?? 1,
            UsesRealTime = realTime, ExpiresAt = expiresAt,
            RemainingRealTimeSeconds = status is { TimeBasis: AbilityTimeBasis.RealTime } timed
                ? timed.RemainingRealTime.TotalSeconds : null
        };
    }

    private static AbilitySnapshot CaptureAbility(Unit unit, AbilityBase ability, TimeSpan now)
    {
        var command = (ICommandAbility)ability;
        CommandGateManager? gate = command.GateManager;
        double cooldown = gate == null ? 0 : Math.Max(0, (gate.NextReadyTime - now).TotalSeconds);
        bool authorized = ability.Binding.SourceInstanceId == null || ability.Binding.IsAuthorized(unit.Id);
        bool available = unit.CanParticipate && unit.MapObject?.CanCast == true && authorized &&
            (gate == null || unit.ActionGate?.CheckAvailability(now, unit, gate).CanExecute == true);
        return new()
        {
            RuntimeInstanceId = ability.RuntimeInstanceId, DefinitionId = ability.Id, Name = ability.Name ?? ability.Id,
            Level = ability.Level, SourceInstanceId = ability.Binding.SourceInstanceId,
            SourceCategory = ability.Binding.Source?.Category, CanActivate = available,
            RemainingCooldownSeconds = cooldown, AvailableCharges = gate?.GetChargeSnapshot()?.Available
        };
    }
}
