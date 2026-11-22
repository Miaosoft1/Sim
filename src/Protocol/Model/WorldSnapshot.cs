namespace Miaosoft.Sim.Protocol.Model;

public readonly record struct WorldPosition(double X, double Y);

public sealed record AbilitySnapshot
{
    public required Guid RuntimeInstanceId { get; init; }
    public required string DefinitionId { get; init; }
    public required string Name { get; init; }
    public required int Level { get; init; }
    public Guid? SourceInstanceId { get; init; }
    public string? SourceCategory { get; init; }
    public bool CanActivate { get; init; }
    public double RemainingCooldownSeconds { get; init; }
    public int? AvailableCharges { get; init; }
}

public sealed record UnitSnapshot
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public int TeamId { get; init; }
    public required bool IsAlive { get; init; }
    public bool CanMove { get; init; } = true;
    public bool CanCast { get; init; } = true;
    public Guid? DeathId { get; init; }
    public required WorldPosition Position { get; init; }
    public Guid? MapInstanceId { get; init; }
    public long ControlVersion { get; init; }
    public int Life { get; init; }
    public int MaximumLife { get; init; }
    public decimal Energy { get; init; }
    public decimal MaximumEnergy { get; init; }
    public int Level { get; init; }
    public int? Experience { get; init; }
    public int? MaximumExperience { get; init; }
    public double MoveSpeed { get; init; }
    public IReadOnlyList<AbilitySnapshot> Abilities { get; init; } = [];
    public IReadOnlyList<StatusSnapshot> Statuses { get; init; } = [];
}

public sealed record WorldSnapshot
{
    public bool IsCompleted { get; init; }
    public required TimeSpan Time { get; init; }
    public double TimeScale { get; init; } = 1;
    public IReadOnlyList<UnitSnapshot> Units { get; init; } = [];
    public IReadOnlyList<MapSnapshot> Maps { get; init; } = [];
    public IReadOnlyList<ProjectileSnapshot> Projectiles { get; init; } = [];
}

public interface IWorld
{
    WorldSnapshot GetWorldSnapshot();
}
