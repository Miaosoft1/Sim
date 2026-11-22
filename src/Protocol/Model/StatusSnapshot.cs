namespace Miaosoft.Sim.Protocol.Model;

public sealed record StatusSnapshot
{
    public required Guid RuntimeInstanceId { get; init; }
    public Guid? ApplicationId { get; init; }
    public required string DefinitionId { get; init; }
    public required string Name { get; init; }
    public Guid? SourceUnitId { get; init; }
    public int Stacks { get; init; } = 1;
    public bool UsesRealTime { get; init; }
    public TimeSpan? ExpiresAt { get; init; }
    public double? RemainingRealTimeSeconds { get; init; }
}
