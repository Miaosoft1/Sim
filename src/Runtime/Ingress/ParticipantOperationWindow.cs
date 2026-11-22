using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Runtime.Ingress;

internal sealed class ParticipantOperationWindow
{
    private sealed record Accepted(EventEnvelope Message, OperationReceipt Receipt);
    private readonly Dictionary<long, Accepted> recent = [];
    public long NextSequence { get; private set; } = 1;

    public OperationReceipt? Assess(long sequence, EventEnvelope message)
    {
        if (sequence <= 0 || sequence == long.MaxValue)
            return Rejected(sequence, OperationQueueStatus.Invalid, "The operation sequence is invalid or exhausted.");
        if (sequence > NextSequence)
            return Rejected(sequence, OperationQueueStatus.SequenceGap, "Submit the expected next operation first.");
        if (sequence == NextSequence) return null;
        if (!recent.TryGetValue(sequence, out Accepted? accepted))
            return Rejected(sequence, OperationQueueStatus.Expired, "The original acknowledgement has expired; do not replay as a new operation.");
        return accepted.Message == message ? accepted.Receipt :
            Rejected(sequence, OperationQueueStatus.SequenceConflict, "The same sequence was already used for different content.");
    }

    public OperationReceipt Commit(long sequence, EventEnvelope message)
    {
        if (sequence != NextSequence || sequence <= 0 || sequence == long.MaxValue)
            throw new InvalidOperationException("Only a newly enqueued next operation may advance the receipt window.");
        long next = checked(sequence + 1);
        var receipt = new OperationReceipt(sequence, OperationQueueStatus.Enqueued, next);
        recent.Add(sequence, new(message, receipt));
        NextSequence = next;
        if (recent.Count > SessionProtocol.OperationReceiptWindow)
            recent.Remove(sequence - SessionProtocol.OperationReceiptWindow);
        return receipt;
    }

    public OperationReceipt Rejected(long sequence, OperationQueueStatus status, string reason) =>
        new(sequence, status, NextSequence, reason);
}
