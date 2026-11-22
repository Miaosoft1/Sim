using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Protocol.Event;

namespace Miaosoft.Sim.Runtime;

public sealed record SessionInput(
    Guid ExecutionId, Guid ParticipantId, Guid UnitId, long ConnectionVersion,
    long Sequence, Guid CorrelationId, IEventMessage Message);

public sealed record SessionInputAssessment(bool Allowed, OperationQueueStatus Failure, string? Reason = null);

public interface ISessionSimulation
{
    Context Context { get; }
    IReadOnlyList<Guid> ParticipantUnitIds { get; }
    bool IsCompleted { get; }
    bool CanAcceptCompletedInput(IEventMessage message);
    bool CanAcceptCompletedPostedEvent(IEvent ev) => false;
    SessionInputAssessment AssessInput(Guid unitId, IEventMessage message);
    IEnumerable<IEvent> CreateInputEvents(SessionInput input);
    IEnumerable<IEvent> CreateStartedEvents();
    IEnumerable<IEvent> CreateCompletionEvents() =>
        throw new NotSupportedException("This simulation has no host-requested completion capability.");
    IEnumerable<IEvent> CreateCompletedEvents() => [];
    WorldSnapshot CaptureWorld(Guid? unitId);
    IReadOnlyList<ITransientEventMessage> CaptureTransientEvents(IEvent ev, Guid? unitId) => [];
}
