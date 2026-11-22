using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Hub;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Server.Hosting;

internal sealed class SessionHub(SessionRuntime session, ILogger<SessionHub> logger)
    : Hub<ISessionHubClient>, ISessionHubServer
{
    public Task<SessionWelcome> EnterDemo(SessionAdmissionRequest request) =>
        Invoke(() => session.EnterDemoAsync(Context.ConnectionId, request, Context.ConnectionAborted));
    public Task<PreparedSessionSnapshot> Rejoin(SessionRejoinRequest request) =>
        Invoke(() => session.RejoinAsync(Context.ConnectionId, request, Context.ConnectionAborted));
    public Task<PreparedSessionSnapshot> RequestSnapshot(long connectionVersion) =>
        Invoke(() => session.RequestSnapshotAsync(Context.ConnectionId, connectionVersion, Context.ConnectionAborted));
    public Task<SnapshotAcknowledgement> AcknowledgeSnapshot(SnapshotAcknowledgementRequest request) =>
        Invoke(() => session.AcknowledgeAsync(Context.ConnectionId, request, Context.ConnectionAborted));
    public Task<IReadOnlyList<OperationReceipt>> SubmitEvents(EventBatch batch) =>
        Invoke(() => session.SubmitAsync(Context.ConnectionId, batch, Context.ConnectionAborted));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try { await session.DisconnectAsync(Context.ConnectionId).ConfigureAwait(false); }
        catch (SessionRequestException error) when (error.Failure == SessionRequestFailure.Unavailable)
        {
            logger.LogDebug("The session owner already stopped while detaching connection {Connection}.", Context.ConnectionId);
        }
        finally { await base.OnDisconnectedAsync(exception).ConfigureAwait(false); }
    }

    private static async Task<T> Invoke<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (SessionRequestException error) { throw new HubException($"{error.Failure}: {error.Message}"); }
    }
}
