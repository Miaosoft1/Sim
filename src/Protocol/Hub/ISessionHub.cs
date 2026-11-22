using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Protocol.Hub;

public interface ISessionHubServer
{
    Task<SessionWelcome> EnterDemo(SessionAdmissionRequest request);
    Task<PreparedSessionSnapshot> Rejoin(SessionRejoinRequest request);
    Task<PreparedSessionSnapshot> RequestSnapshot(long connectionVersion);
    Task<SnapshotAcknowledgement> AcknowledgeSnapshot(SnapshotAcknowledgementRequest request);
    Task<IReadOnlyList<OperationReceipt>> SubmitEvents(EventBatch batch);
}

public interface ISessionHubClient
{
    Task ReceiveFrame(ObservationFrame frame);
}
