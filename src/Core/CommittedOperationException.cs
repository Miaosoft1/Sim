namespace Miaosoft.Sim.Core;

public sealed class CommittedOperationException(Guid operationId, Exception inner)
    : Exception($"Operation '{operationId}' was committed, but post-commit processing failed.", inner)
{
    public Guid OperationId { get; } = operationId;
}
