using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Protocol.Model;

public static class SessionProtocol
{
    public const int Version = 5;
    public const int OperationReceiptWindow = 1024;
}

public sealed record SessionCredentials(Guid SessionId, Guid ParticipantId, string Token)
{
    public override string ToString() =>
        $"SessionCredentials {{ SessionId = {SessionId}, ParticipantId = {ParticipantId}, Token = [redacted] }}";
}

public sealed record SessionBinding(Guid SessionId, Guid ParticipantId, Guid? UnitId, long ConnectionVersion);

public sealed record PreparedSessionSnapshot(
    Guid SnapshotId, SessionBinding Binding, long NextOperationSequence, ObservationFrame Frame);

public sealed record SessionWelcome(SessionCredentials Credentials, PreparedSessionSnapshot Snapshot);

public sealed record SessionAdmissionRequest
{
    public int ProtocolVersion { get; init; }
    public Guid RequestId { get; init; }
    public IReadOnlyList<ModuleVersion> Modules { get; init; } = [];
}

public sealed record SessionRejoinRequest
{
    public int ProtocolVersion { get; init; }
    public required SessionCredentials Credentials { get; init; }
    public IReadOnlyList<ModuleVersion> Modules { get; init; } = [];
}

public enum SnapshotAcknowledgement { Waiting, Started, Stale }

public sealed record SnapshotAcknowledgementRequest(long ConnectionVersion, Guid SnapshotId);

public enum OperationQueueStatus
{
    Enqueued, Invalid, Unauthorized, NotReady, QueueFull,
    SequenceGap, SequenceConflict, Expired, Completed
}

public sealed record OperationReceipt(
    long Sequence, OperationQueueStatus Status, long? NextOperationSequence, string? Reason = null);

public enum OperationExecutionStatus { Unspecified, Succeeded, Rejected, Aborted, Unknown }

public sealed record OperationResult(
    long OperationSequence, Guid CorrelationId, OperationExecutionStatus Status, string? Reason) : IEventMessage
{
    public OperationResult() : this(0, Guid.Empty, OperationExecutionStatus.Unspecified, null) { }
}
