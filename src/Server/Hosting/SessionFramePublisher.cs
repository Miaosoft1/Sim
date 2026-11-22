using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Miaosoft.Sim.Protocol.Hub;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Server.Hosting;

internal sealed class SessionFramePublisher : BackgroundService
{
    private sealed record Delivery(string Connection, ObservationFrame Frame);
    private readonly Channel<Delivery> outgoing = Channel.CreateBounded<Delivery>(
        new BoundedChannelOptions(2048) { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly SessionRuntime session;
    private readonly IHubContext<SessionHub, ISessionHubClient> clients;

    public SessionFramePublisher(SessionRuntime session, IHubContext<SessionHub, ISessionHubClient> clients)
    {
        this.session = session;
        this.clients = clients;
        session.FramePublished += QueueFrame;
    }

    private void QueueFrame(string connection, ObservationFrame frame)
    {
        if (!outgoing.Writer.TryWrite(new(connection, frame)))
            throw new InvalidOperationException("The bounded session publication queue is full or closed.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (Delivery delivery in outgoing.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await clients.Clients.Client(delivery.Connection).ReceiveFrame(delivery.Frame).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        session.FramePublished -= QueueFrame;
        outgoing.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
