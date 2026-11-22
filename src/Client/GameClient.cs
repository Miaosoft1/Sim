using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using Miaosoft.Sim.Client.Observation;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Hub;
using Miaosoft.Sim.Protocol.Model;
using YamlDotNet.Core;

namespace Miaosoft.Sim.Client;

public sealed record PendingSubmission(
    ParticipantOperation Operation, bool Uncertain, OperationReceipt? LastReceipt = null);

public sealed class GameClient : IAsyncDisposable
{
    private readonly EventCodecRegistry codec;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly ConcurrentDictionary<long, OperationResult> outcomes = [];
    private TaskCompletionSource changed = NewSignal();
    private HubConnection? connection;
    private Uri? endpoint;
    private long transportEpoch;
    private long nextSequence = 1;
    private PendingSubmission? pending;
    private Exception? observationFailure;

    public GameClient(EventCodecRegistry? codec = null, IEnumerable<IClientObservationModule>? observationModules = null)
    {
        this.codec = codec ?? EventCodec.CreateBuilder().Build();
        World = new(this.codec, observationModules);
    }

    public ClientWorld World { get; }
    public SessionCredentials? Credentials { get; private set; }
    public PendingSubmission? Pending => Volatile.Read(ref pending);
    public Exception? ObservationFailure => Volatile.Read(ref observationFailure);
    public event Action<ClientObservationState>? ObservationChanged;
    public event Action<Exception?>? ConnectionLost;
    public event Action<Exception>? ObservationRejected;

    public async Task<SessionWelcome> EnterDemoAsync(Uri address, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Credentials != null) throw new InvalidOperationException("Use rejoin for an existing participant.");
            await Connect(address, cancellationToken).ConfigureAwait(false);
            SessionWelcome welcome = await RequireConnection().InvokeAsync<SessionWelcome>(nameof(ISessionHubServer.EnterDemo),
                new SessionAdmissionRequest
                {
                    ProtocolVersion = SessionProtocol.Version, RequestId = Guid.NewGuid(), Modules = World.Capabilities
                }, cancellationToken).ConfigureAwait(false);
            if (welcome.Credentials.SessionId != welcome.Snapshot.Binding.SessionId ||
                welcome.Credentials.ParticipantId != welcome.Snapshot.Binding.ParticipantId)
                throw new InvalidDataException("The admission credential belongs to another snapshot.");
            Credentials = welcome.Credentials;
            ApplyPrepared(welcome.Snapshot);
            PreparedSessionSnapshot acknowledged = await Acknowledge(welcome.Snapshot, cancellationToken).ConfigureAwait(false);
            return welcome with { Snapshot = acknowledged };
        }
        finally { lifecycle.Release(); }
    }

    public async Task<PreparedSessionSnapshot> RejoinAsync(
        Uri? address = null, SessionCredentials? credentials = null, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionCredentials ticket = credentials ?? Credentials ??
                throw new InvalidOperationException("A server-issued rejoin credential is required.");
            if (Credentials is { } current &&
                (current.SessionId != ticket.SessionId || current.ParticipantId != ticket.ParticipantId))
                throw new InvalidOperationException("Use a new client instance for a different participant.");
            await Connect(address ?? endpoint ?? throw new InvalidOperationException("A session endpoint is required."),
                cancellationToken).ConfigureAwait(false);
            PreparedSessionSnapshot snapshot = await RequireConnection().InvokeAsync<PreparedSessionSnapshot>(
                nameof(ISessionHubServer.Rejoin), new SessionRejoinRequest
                {
                    ProtocolVersion = SessionProtocol.Version, Credentials = ticket, Modules = World.Capabilities
                }, cancellationToken).ConfigureAwait(false);
            if (snapshot.Binding.SessionId != ticket.SessionId || snapshot.Binding.ParticipantId != ticket.ParticipantId)
                throw new InvalidDataException("Rejoin returned another participant's snapshot.");
            Credentials = ticket;
            ApplyPrepared(snapshot);
            return await Acknowledge(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }

    public async Task<PreparedSessionSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PreparedSessionSnapshot snapshot = await RequireConnection().InvokeAsync<PreparedSessionSnapshot>(
                nameof(ISessionHubServer.RequestSnapshot), World.State.Binding.ConnectionVersion, cancellationToken).ConfigureAwait(false);
            ApplyPrepared(snapshot);
            return await Acknowledge(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }

    public async Task<OperationReceipt> SendAsync(IEventMessage message, CancellationToken cancellationToken = default)
    {
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending != null) throw new InvalidOperationException("Retry or explicitly clear the pending original operation before sending another.");
            if (nextSequence == long.MaxValue) throw new InvalidOperationException("The operation sequence has been exhausted.");
            var operation = new ParticipantOperation(nextSequence, codec.Encode(message, EventDirection.ClientToServer));
            Volatile.Write(ref pending, new(operation, false));
            return await SendPending(cancellationToken).ConfigureAwait(false);
        }
        finally { sending.Release(); }
    }

    public async Task<OperationReceipt> RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SendPending(cancellationToken).ConfigureAwait(false); }
        finally { sending.Release(); }
    }

    public async Task ClearUnqueuedPendingAsync(CancellationToken cancellationToken = default)
    {
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending == null) return;
            if (pending.Uncertain || pending.LastReceipt == null ||
                pending.LastReceipt.Status is OperationQueueStatus.Enqueued or OperationQueueStatus.Expired)
                throw new InvalidOperationException("An uncertain or accepted operation cannot be silently discarded or resubmitted under a new number.");
            Volatile.Write(ref pending, null);
        }
        finally { sending.Release(); }
    }

    /// <summary>Submits caller-retained original operations; callers must preserve their sequence and payload on retry.</summary>
    public async Task<IReadOnlyList<OperationReceipt>> SubmitBatchAsync(EventBatch batch, CancellationToken cancellationToken = default)
    {
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending != null) throw new InvalidOperationException("A retained operation must be resolved first.");
            IReadOnlyList<OperationReceipt> receipts = await RequireConnection().InvokeAsync<IReadOnlyList<OperationReceipt>>(
                nameof(ISessionHubServer.SubmitEvents), batch, cancellationToken).ConfigureAwait(false);
            if (receipts.Count != batch.Operations.Count || receipts.Where((receipt, index) =>
                    receipt == null || receipt.Sequence != batch.Operations[index].Sequence || !Enum.IsDefined(receipt.Status) ||
                    receipt.Status == OperationQueueStatus.Enqueued &&
                        (receipt.NextOperationSequence == null || receipt.NextOperationSequence <= receipt.Sequence)).Any())
                throw new InvalidDataException("The server returned a mismatched batch acknowledgement.");
            foreach (OperationReceipt receipt in receipts)
                if (receipt.Status == OperationQueueStatus.Enqueued && receipt.NextOperationSequence is { } next)
                    nextSequence = Math.Max(nextSequence, next);
            return receipts;
        }
        finally { sending.Release(); }
    }

    public async Task<OperationResult> WaitForOutcomeAsync(long sequence, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal = Volatile.Read(ref changed).Task;
            if (outcomes.TryGetValue(sequence, out OperationResult? outcome)) return outcome;
            if (ObservationFailure is { } error) throw new InvalidDataException("Observation synchronization failed.", error);
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DisconnectCore(cancellationToken).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task<OperationReceipt> SendPending(CancellationToken cancellationToken)
    {
        PendingSubmission original = pending ?? throw new InvalidOperationException("There is no retained operation to retry.");
        bool received = false;
        try
        {
            SessionBinding binding = World.State.Binding;
            IReadOnlyList<OperationReceipt> receipts = await RequireConnection().InvokeAsync<IReadOnlyList<OperationReceipt>>(
                nameof(ISessionHubServer.SubmitEvents), new EventBatch
                {
                    ParticipantId = binding.ParticipantId, ConnectionVersion = binding.ConnectionVersion,
                    Operations = [original.Operation]
                }, cancellationToken).ConfigureAwait(false);
            if (receipts.Count != 1 || receipts[0].Sequence != original.Operation.Sequence)
                throw new InvalidDataException("The server returned a mismatched operation acknowledgement.");
            OperationReceipt receipt = receipts[0];
            received = true;
            if (receipt.Status == OperationQueueStatus.Enqueued)
            {
                if (receipt.NextOperationSequence is not { } next || next <= original.Operation.Sequence)
                    throw new InvalidDataException("Queue acceptance omitted its next operation sequence.");
                nextSequence = Math.Max(nextSequence, next);
                Volatile.Write(ref pending, null);
            }
            else Volatile.Write(ref pending, original with { LastReceipt = receipt });
            return receipt;
        }
        finally
        {
            if (!received) Volatile.Write(ref pending, original with { Uncertain = true });
        }
    }

    private async Task Connect(Uri address, CancellationToken cancellationToken)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") ||
            address.Scheme == "http" && !address.IsLoopback)
            throw new ArgumentException("Session endpoints require loopback HTTP or HTTPS.", nameof(address));
        await DisconnectCore(cancellationToken).ConfigureAwait(false);
        endpoint = address;
        long epoch = Interlocked.Increment(ref transportEpoch);
        HubConnection transport = new HubConnectionBuilder().WithUrl(address).Build();
        transport.On<ObservationFrame>(nameof(ISessionHubClient.ReceiveFrame), frame => Receive(epoch, frame));
        transport.Closed += error =>
        {
            if (epoch == Volatile.Read(ref transportEpoch)) ConnectionLost?.Invoke(error);
            return Task.CompletedTask;
        };
        connection = transport;
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectCore(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref transportEpoch);
        HubConnection? previous = Interlocked.Exchange(ref connection, null);
        if (previous == null) return;
        try { await previous.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { await previous.DisposeAsync().ConfigureAwait(false); }
    }

    private void ApplyPrepared(PreparedSessionSnapshot snapshot)
    {
        World.Prepare(snapshot);
        nextSequence = Math.Max(nextSequence, snapshot.NextOperationSequence);
        Volatile.Write(ref observationFailure, null);
        PublishObservation();
    }

    private async Task<PreparedSessionSnapshot> Acknowledge(PreparedSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        while (await RequireConnection().InvokeAsync<SnapshotAcknowledgement>(nameof(ISessionHubServer.AcknowledgeSnapshot),
            new SnapshotAcknowledgementRequest(snapshot.Binding.ConnectionVersion, snapshot.SnapshotId),
            cancellationToken).ConfigureAwait(false) == SnapshotAcknowledgement.Stale)
        {
            snapshot = await RequireConnection().InvokeAsync<PreparedSessionSnapshot>(nameof(ISessionHubServer.RequestSnapshot),
                snapshot.Binding.ConnectionVersion, cancellationToken).ConfigureAwait(false);
            ApplyPrepared(snapshot);
        }
        return snapshot;
    }

    private void Receive(long epoch, ObservationFrame frame)
    {
        if (epoch != Volatile.Read(ref transportEpoch)) return;
        try
        {
            if (World.Apply(frame) == FrameApplication.Applied) PublishObservation();
        }
        catch (InvalidDataException error) { RejectObservation(error); throw; }
        catch (YamlException error) { RejectObservation(error); throw; }
        catch (ArgumentException error) { RejectObservation(error); throw; }
    }

    private void PublishObservation()
    {
        ClientObservationState state = World.State;
        foreach (OperationResult outcome in state.Events.OfType<OperationResult>())
        {
            if (outcomes.TryGetValue(outcome.OperationSequence, out OperationResult? existing) && existing != outcome)
                throw new InvalidDataException("The server published conflicting business outcomes.");
            outcomes[outcome.OperationSequence] = outcome;
        }
        while (outcomes.Count > SessionProtocol.OperationReceiptWindow)
            outcomes.TryRemove(outcomes.Keys.Min(), out _);
        Interlocked.Exchange(ref changed, NewSignal()).TrySetResult();
        ObservationChanged?.Invoke(state);
    }

    private void RejectObservation(Exception error)
    {
        Volatile.Write(ref observationFailure, error);
        Interlocked.Exchange(ref changed, NewSignal()).TrySetResult();
        ObservationRejected?.Invoke(error);
    }

    private HubConnection RequireConnection() => connection is { State: HubConnectionState.Connected } value
        ? value : throw new InvalidOperationException("The session transport is not connected.");
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        lifecycle.Dispose();
        sending.Dispose();
    }
}
