using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Runtime.EventData;

public sealed record SessionOperationCompletedEvent : Event
{
    public required Guid ExecutionId { get; init; }
    public required OperationExecutionStatus Status { get; init; }
    public string? Reason { get; init; }
}
