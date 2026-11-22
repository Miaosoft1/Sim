using System.Text;

namespace Miaosoft.Sim.Protocol.Event;

public sealed record EventEnvelope
{
    public const int CurrentVersion = 2;
    public int Version { get; init; } = CurrentVersion;
    public string EventId { get; init; } = "";
    public Guid? CorrelationId { get; init; }
    public string Payload { get; init; } = "";
    public int WireSizeUpperBound => checked(512 + 6 *
        (Encoding.UTF8.GetByteCount(EventId) + Encoding.UTF8.GetByteCount(Payload)));
}

public sealed record ParticipantOperation(long Sequence, EventEnvelope Event);

public sealed record EventBatch
{
    public const int MaximumWireBytes = 32 * 1024;
    public const int MaximumOperations = 256;
    public Guid ParticipantId { get; init; }
    public long ConnectionVersion { get; init; }
    public IReadOnlyList<ParticipantOperation> Operations { get; init; } = [];

    public void Validate()
    {
        if (ParticipantId == Guid.Empty || ConnectionVersion < 1)
            throw new ArgumentException("A bound participant and connection version are required.");
        ArgumentNullException.ThrowIfNull(Operations);
        if (Operations.Count is < 1 or > MaximumOperations)
            throw new ArgumentException($"A batch must contain 1..{MaximumOperations} operations.");
        long bytes = 256;
        foreach (ParticipantOperation operation in Operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(operation.Event);
            if (operation.Event.CorrelationId is null || operation.Event.CorrelationId == Guid.Empty)
                throw new ArgumentException("Each operation requires a correlation ID.");
            bytes = checked(bytes + operation.Event.WireSizeUpperBound);
        }
        if (bytes > MaximumWireBytes)
            throw new ArgumentException("The batch exceeds the transport size limit.");
    }
}
