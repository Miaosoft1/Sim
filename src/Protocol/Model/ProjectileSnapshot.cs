namespace Miaosoft.Sim.Protocol.Model;

public sealed record ProjectileSnapshot
{
    public required Guid Id { get; init; }
    public required Guid MapInstanceId { get; init; }
    public required Guid SourceUnitId { get; init; }
    public Guid? TargetUnitId { get; init; }
    public required string AbilityId { get; init; }
    public Guid? AbilityInstanceId { get; init; }
    public required WorldPosition Position { get; init; }
    public required WorldPosition Destination { get; init; }
    public required double Speed { get; init; }
    public required string Mode { get; init; }
}
