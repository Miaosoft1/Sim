using System.Security.Cryptography;
using System.Threading.Channels;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime.EventData;
using Miaosoft.Sim.Runtime.Ingress;
using YamlDotNet.Core;

namespace Miaosoft.Sim.Runtime;

public sealed class SessionRuntime : IAsyncDisposable
{
    private interface IRequest
    {
        void Execute();
        void Cancel();
    }

    private sealed class Request<T>(Func<T> action, CancellationToken cancellation) : IRequest
    {
        public TaskCompletionSource<T> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute()
        {
            if (cancellation.IsCancellationRequested) { Response.TrySetCanceled(cancellation); return; }
            try { Response.TrySetResult(action()); }
            catch (SessionRequestException error) { Response.TrySetException(error); }
            catch (Exception error) { Response.TrySetException(error); throw; }
        }
        public void Cancel() => Response.TrySetCanceled();
    }

    private sealed class Participant(Guid id, Guid unitId, SessionCredentials credentials)
    {
        public Guid Id { get; } = id;
        public Guid? UnitId { get; set; } = unitId;
        public SessionCredentials Credentials { get; } = credentials;
        public ParticipantOperationWindow Operations { get; } = new();
        public string? Connection { get; set; }
        public long ConnectionVersion { get; set; }
        public Guid? AdmissionRequestId { get; set; }
        public bool Claimed { get; set; }
        public bool Acknowledged { get; set; }
        public long PublicationSequence { get; set; }
        public PreparedSessionSnapshot? Prepared { get; set; }
        public Dictionary<string, ISessionObservationModule> Modules { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> PublishedModules { get; set; } = new(StringComparer.Ordinal);
        public Queue<ObservationFrame> BufferedFrames { get; } = [];
        public List<EventEnvelope> Results { get; } = [];
        public List<EventEnvelope> Transients { get; } = [];
    }

    private sealed record Captured(ObservationFrame Frame, Dictionary<string, string?> Modules);
    private readonly ISessionSimulation simulation;
    private readonly EventCodecRegistry codec;
    private readonly Context context;
    private readonly EventReceiver receiver;
    private readonly List<SessionResultRecord> resultRecords = [];
    private readonly Dictionary<Guid, Participant> participants = [];
    private readonly Dictionary<string, Participant> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ISessionObservationModule> modules;
    private readonly Channel<IRequest> requests = Channel.CreateBounded<IRequest>(
        new BoundedChannelOptions(1024) { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Queue<SessionInput> pending = [];
    private readonly Dictionary<Guid, SessionInput> inFlight = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly bool demoAdmission;
    private readonly int inputCapacity;
    private readonly Task loop;
    private readonly Lazy<Task> disposal;
    private IEnumerator<IEvent>? activeInput;
    private bool started;
    private bool completionObserved;
    private bool dirty;
    private long version;

    public SessionRuntime(
        ISessionSimulation simulation, EventCodecRegistry codec,
        IEnumerable<ISessionObservationModule>? observationModules = null,
        bool enableDemoAdmission = false, int inputCapacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(codec);
        this.simulation = simulation;
        this.codec = codec;
        context = simulation.Context;
        if (!context.Realtime || context.IsProcessingEvent)
            throw new ArgumentException("A session needs an unowned realtime context.", nameof(simulation));
        if (inputCapacity < 1) throw new ArgumentOutOfRangeException(nameof(inputCapacity));
        this.inputCapacity = inputCapacity;
        demoAdmission = enableDemoAdmission;
        modules = (observationModules ?? []).ToDictionary(module => module.Id, StringComparer.Ordinal);
        if (modules.Values.Any(module => string.IsNullOrWhiteSpace(module.Id) || module.Version < 1))
            throw new ArgumentException("Observation modules require distinct IDs and positive versions.", nameof(observationModules));
        if (simulation.ParticipantUnitIds.Count == 0 || simulation.ParticipantUnitIds.Any(id => id == Guid.Empty) ||
            simulation.ParticipantUnitIds.Distinct().Count() != simulation.ParticipantUnitIds.Count)
            throw new ArgumentException("A session requires distinct preconfigured participant units.", nameof(simulation));
        foreach (Guid unit in simulation.ParticipantUnitIds)
        {
            Guid participant = Guid.NewGuid();
            participants.Add(participant, new(participant, unit,
                new(Id, participant, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));
        }
        receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        context.EventProcessed += OnProcessed;
        context.TimeScaleChanged += OnTimeScaleChanged;
        disposal = new(DisposeCoreAsync);
        loop = Task.Run(RunAsync);
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Task Completion => loop;
    public event Action<string, ObservationFrame>? FramePublished;

    public Task<SessionWelcome> EnterDemoAsync(
        string connection, SessionAdmissionRequest request, CancellationToken cancellationToken = default) =>
        Call(() => EnterDemo(connection, request), cancellationToken);

    public Task<PreparedSessionSnapshot> RejoinAsync(
        string connection, SessionRejoinRequest request, CancellationToken cancellationToken = default) =>
        Call(() =>
        {
            if (request == null) throw Fault(SessionRequestFailure.InvalidRequest, "A rejoin request is required.");
            ValidateProtocol(request.ProtocolVersion);
            SessionCredentials credentials = request.Credentials;
            if (credentials == null || credentials.SessionId != Id ||
                !participants.TryGetValue(credentials.ParticipantId, out Participant? participant) ||
                !TokenMatches(participant.Credentials.Token, credentials.Token))
                throw Fault(SessionRequestFailure.Unauthorized, "The session credential is invalid.");
            return Bind(connection, participant, SelectModules(request.Modules));
        }, cancellationToken);

    public Task<PreparedSessionSnapshot> RequestSnapshotAsync(
        string connection, long connectionVersion, CancellationToken cancellationToken = default) =>
        Call(() => Prepare(Bound(connection, connectionVersion)), cancellationToken);

    public Task<SnapshotAcknowledgement> AcknowledgeAsync(
        string connection, SnapshotAcknowledgementRequest request, CancellationToken cancellationToken = default) =>
        Call(() => request == null
            ? throw Fault(SessionRequestFailure.InvalidRequest, "A snapshot acknowledgement is required.")
            : Acknowledge(Bound(connection, request.ConnectionVersion), request.SnapshotId), cancellationToken);

    public Task<IReadOnlyList<OperationReceipt>> SubmitAsync(
        string connection, EventBatch batch, CancellationToken cancellationToken = default) =>
        Call(() => Submit(connection, batch), cancellationToken);

    public Task RequestCompletionAsync(CancellationToken cancellationToken = default) =>
        Call(() =>
        {
            if (!started) throw Fault(SessionRequestFailure.InvalidRequest, "The session has not started.");
            if (!simulation.IsCompleted)
                foreach (IEvent ev in simulation.CreateCompletionEvents()) context.Dispatch(ev).ToList();
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<SessionResultRecord>> GetResultRecordsAsync(CancellationToken cancellationToken = default) =>
        Call<IReadOnlyList<SessionResultRecord>>(() => Array.AsReadOnly(resultRecords.ToArray()), cancellationToken);

    public Task DisconnectAsync(string connection, CancellationToken cancellationToken = default) =>
        Call(() =>
        {
            if (connections.Remove(connection, out Participant? participant))
            {
                participant.Connection = null;
                participant.Acknowledged = false;
                participant.BufferedFrames.Clear();
                participant.Transients.Clear();
            }
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<SessionCredentials>> GetAdmissionCredentialsAsync(CancellationToken cancellationToken = default) =>
        Call<IReadOnlyList<SessionCredentials>>(() => Array.AsReadOnly(
            participants.Values.Select(participant => participant.Credentials).ToArray()), cancellationToken);

    public Task SetControlledUnitAsync(Guid participantId, Guid? unitId, CancellationToken cancellationToken = default) =>
        Call(() =>
        {
            if (!participants.TryGetValue(participantId, out Participant? participant))
                throw Fault(SessionRequestFailure.InvalidRequest, "The participant is unknown.");
            if (unitId == Guid.Empty || unitId.HasValue && !simulation.ParticipantUnitIds.Contains(unitId.Value))
                throw Fault(SessionRequestFailure.InvalidRequest, "The controlled unit is not registered.");
            if (unitId.HasValue && participants.Values.Any(other => !ReferenceEquals(other, participant) && other.UnitId == unitId))
                throw Fault(SessionRequestFailure.Conflict, "The unit is already controlled by another participant.");
            participant.UnitId = unitId;
            dirty = true;
            return true;
        }, cancellationToken);

    private SessionWelcome EnterDemo(string connection, SessionAdmissionRequest request)
    {
        if (!demoAdmission) throw Fault(SessionRequestFailure.DemoDisabled, "Demo admission was not enabled.");
        if (request == null) throw Fault(SessionRequestFailure.InvalidRequest, "An admission request is required.");
        if (string.IsNullOrWhiteSpace(connection))
            throw Fault(SessionRequestFailure.InvalidRequest, "A connection identity is required.");
        ValidateProtocol(request.ProtocolVersion);
        if (request.RequestId == Guid.Empty)
            throw Fault(SessionRequestFailure.InvalidRequest, "An admission request ID is required.");
        Dictionary<string, ISessionObservationModule> selected = SelectModules(request.Modules);
        if (connections.TryGetValue(connection, out Participant? existing))
        {
            if (existing.AdmissionRequestId != request.RequestId || existing.Prepared == null ||
                !existing.Modules.Keys.Order().SequenceEqual(selected.Keys.Order()))
                throw Fault(SessionRequestFailure.Conflict, "The connection already belongs to another admission.");
            return new(existing.Credentials, existing.Prepared);
        }
        Participant participant = participants.Values.FirstOrDefault(candidate => !candidate.Claimed)
            ?? throw Fault(SessionRequestFailure.SessionFull, "The preconfigured session is full.");
        PreparedSessionSnapshot snapshot = Bind(connection, participant, selected);
        participant.Claimed = true;
        participant.AdmissionRequestId = request.RequestId;
        return new(participant.Credentials, snapshot);
    }

    private PreparedSessionSnapshot Bind(
        string connection, Participant participant, Dictionary<string, ISessionObservationModule> selected)
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw Fault(SessionRequestFailure.InvalidRequest, "A connection identity is required.");
        if (connections.TryGetValue(connection, out Participant? bound) && !ReferenceEquals(bound, participant))
            throw Fault(SessionRequestFailure.Conflict, "The connection belongs to another participant.");
        if (participant.Modules.Keys.Except(selected.Keys).Any())
            throw Fault(SessionRequestFailure.UnsupportedModule, "Rejoining cannot silently drop an established observation module.");
        long generation = checked(participant.ConnectionVersion + 1);
        long nextVersion = checked(version + 1);
        Captured captured = Capture(participant, selected, new(StringComparer.Ordinal), true, nextVersion, generation);
        if (participant.Connection != null) connections.Remove(participant.Connection);
        participant.Connection = connection;
        participant.ConnectionVersion = generation;
        participant.Modules = selected;
        participant.PublishedModules = captured.Modules;
        participant.PublicationSequence = captured.Frame.PublicationSequence;
        participant.Acknowledged = false;
        participant.BufferedFrames.Clear();
        participant.Results.Clear();
        participant.Transients.Clear();
        participant.Claimed = true;
        connections[connection] = participant;
        version = nextVersion;
        return participant.Prepared = new(Guid.NewGuid(), Binding(participant), participant.Operations.NextSequence, captured.Frame);
    }

    private PreparedSessionSnapshot Prepare(Participant participant)
    {
        Captured captured = Capture(participant, participant.Modules, new(StringComparer.Ordinal), true,
            checked(version + 1), participant.ConnectionVersion);
        version = captured.Frame.StateVersion;
        participant.PublicationSequence = captured.Frame.PublicationSequence;
        participant.PublishedModules = captured.Modules;
        participant.Acknowledged = false;
        participant.BufferedFrames.Clear();
        participant.Results.Clear();
        participant.Transients.Clear();
        return participant.Prepared = new(Guid.NewGuid(), Binding(participant), participant.Operations.NextSequence, captured.Frame);
    }

    private SnapshotAcknowledgement Acknowledge(Participant participant, Guid snapshotId)
    {
        if (participant.Prepared?.SnapshotId != snapshotId) return SnapshotAcknowledgement.Stale;
        participant.Acknowledged = true;
        while (participant.BufferedFrames.TryDequeue(out ObservationFrame? frame))
            FramePublished?.Invoke(participant.Connection!, frame);
        if (!started && participants.Values.All(candidate => candidate.Connection != null && candidate.Acknowledged))
        {
            started = true;
            foreach (IEvent ev in simulation.CreateStartedEvents()) context.Dispatch(ev).ToList();
            dirty = true;
        }
        return started ? SnapshotAcknowledgement.Started : SnapshotAcknowledgement.Waiting;
    }

    private IReadOnlyList<OperationReceipt> Submit(string connection, EventBatch batch)
    {
        if (batch == null) throw Fault(SessionRequestFailure.InvalidRequest, "An event batch is required.");
        Participant participant = Bound(connection, batch.ConnectionVersion);
        if (batch.ParticipantId != participant.Id)
            throw Fault(SessionRequestFailure.Unauthorized, "The batch belongs to another participant.");
        try { batch.Validate(); }
        catch (ArgumentException error) { throw Fault(SessionRequestFailure.InvalidRequest, error.Message); }
        var receipts = new List<OperationReceipt>();
        foreach (ParticipantOperation operation in batch.Operations)
        {
            ParticipantOperationWindow window = participant.Operations;
            OperationReceipt Reject(OperationQueueStatus status, string reason) => status == OperationQueueStatus.Unauthorized
                ? new(operation.Sequence, status, null, reason) : window.Rejected(operation.Sequence, status, reason);
            if (participant.UnitId is not { } unitId) { receipts.Add(Reject(OperationQueueStatus.Unauthorized, "The participant controls no unit.")); continue; }
            IEventMessage message;
            try { message = codec.Decode(operation.Event, EventDirection.ClientToServer); }
            catch (ArgumentException error) { receipts.Add(Reject(OperationQueueStatus.Invalid, error.Message)); continue; }
            catch (YamlException error) { receipts.Add(Reject(OperationQueueStatus.Invalid, error.Message)); continue; }
            SessionInputAssessment assessment = simulation.AssessInput(unitId, message);
            if (!assessment.Allowed && assessment.Failure == OperationQueueStatus.Enqueued)
                throw new InvalidOperationException("Rejected input cannot report queue success.");
            if (!assessment.Allowed) { receipts.Add(Reject(assessment.Failure, assessment.Reason ?? "The input was rejected.")); continue; }
            if (window.Assess(operation.Sequence, operation.Event) is { } prior) { receipts.Add(prior); continue; }
            if (!started || !participant.Acknowledged) { receipts.Add(Reject(OperationQueueStatus.NotReady, "Acknowledge the prepared snapshot first.")); continue; }
            if (simulation.IsCompleted && !simulation.CanAcceptCompletedInput(message))
            { receipts.Add(Reject(OperationQueueStatus.Completed, "The session has completed.")); continue; }
            if (inFlight.Count >= inputCapacity)
            { receipts.Add(Reject(OperationQueueStatus.QueueFull, "The session input queue is full.")); continue; }
            var input = new SessionInput(Guid.NewGuid(), participant.Id, unitId, participant.ConnectionVersion,
                operation.Sequence, operation.Event.CorrelationId!.Value, message);
            pending.Enqueue(input);
            inFlight.Add(input.ExecutionId, input);
            receipts.Add(window.Commit(operation.Sequence, operation.Event));
        }
        return Array.AsReadOnly(receipts.ToArray());
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (!context.HasEventsDueNow)
                {
                    if (activeInput == null)
                    {
                        Publish();
                        int count = requests.Reader.Count;
                        for (int index = 0; index < count && requests.Reader.TryRead(out IRequest? request); index++)
                            request.Execute();
                        if (simulation.IsCompleted && !completionObserved)
                        {
                            context.ClearPendingEvents();
                            context.Inbox.RestrictPostedEvents(simulation.CanAcceptCompletedPostedEvent);
                            context.TimeScale = 0;
                            completionObserved = true;
                            HashSet<Guid> queued = pending.Select(input => input.ExecutionId).ToHashSet();
                            foreach (SessionInput interrupted in inFlight.Values.Where(input => !queued.Contains(input.ExecutionId)).ToArray())
                                context.Dispatch(context.Factory.NewEvent<SessionOperationCompletedEvent>() with
                                {
                                    ExecutionId = interrupted.ExecutionId, Status = OperationExecutionStatus.Unknown,
                                    Reason = "Completion interrupted an accepted execution before its final outcome was published.",
                                    Receiver = receiver
                                }).ToList();
                            foreach (IEvent cleanup in simulation.CreateCompletedEvents()) context.Dispatch(cleanup).ToList();
                        }
                        if (!context.HasEventsDueNow && pending.TryDequeue(out SessionInput? input))
                        {
                            Participant participant = participants[input.ParticipantId];
                            if (participant.ConnectionVersion != input.ConnectionVersion || participant.UnitId != input.UnitId ||
                                participant.Connection == null || !participant.Acknowledged)
                                Complete(input, OperationExecutionStatus.Aborted, "The connection or control authority changed before execution.");
                            else if (simulation.IsCompleted && !simulation.CanAcceptCompletedInput(input.Message))
                                Complete(input, OperationExecutionStatus.Aborted, "The session completed before this input executed.");
                            else
                                activeInput = simulation.CreateInputEvents(input).GetEnumerator();
                        }
                    }
                    if (activeInput != null)
                    {
                        if (activeInput.MoveNext()) context.Dispatch(activeInput.Current).ToList();
                        else { activeInput.Dispose(); activeInput = null; continue; }
                    }
                    else if (pending.Count != 0 || requests.Reader.Count != 0 || dirty)
                        continue;
                }
                ContextStepResult step = await context.NextAsync(stopping.Token).ConfigureAwait(false);
                if (!step.KeepRunning && !simulation.IsCompleted)
                    throw new InvalidOperationException("The session context terminated before its owner was disposed.");
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            requests.Writer.TryComplete();
            while (requests.Reader.TryRead(out IRequest? request)) request.Cancel();
            activeInput?.Dispose();
            context.EventProcessed -= OnProcessed;
            context.TimeScaleChanged -= OnTimeScaleChanged;
        }
    }

    private void OnProcessed(IEvent ev)
    {
        dirty = true;
        if (ev is ISessionResultRecordEvent result) resultRecords.Add(result.CreateResultRecord());
        if (ev is SessionOperationCompletedEvent outcome)
        {
            if (!inFlight.TryGetValue(outcome.ExecutionId, out SessionInput? input))
                throw new InvalidOperationException("The business outcome has no pending session operation.");
            if (!Enum.IsDefined(outcome.Status) || outcome.Status == OperationExecutionStatus.Unspecified)
                throw new InvalidOperationException("The business outcome has no valid execution status.");
            Complete(input, outcome.Status, outcome.Reason);
        }
        foreach (Participant participant in participants.Values.Where(participant =>
            participant.Connection != null && participant.Acknowledged))
            foreach (ITransientEventMessage message in simulation.CaptureTransientEvents(ev, participant.UnitId))
                participant.Transients.Add(codec.Encode(message, EventDirection.ServerToClient));
    }

    private void OnTimeScaleChanged(double value) => dirty = true;

    private void Complete(SessionInput input, OperationExecutionStatus status, string? reason)
    {
        EventEnvelope result = codec.Encode(new OperationResult(input.Sequence, input.CorrelationId, status, reason),
            EventDirection.ServerToClient) with { CorrelationId = input.CorrelationId };
        participants[input.ParticipantId].Results.Add(result);
        inFlight.Remove(input.ExecutionId);
        dirty = true;
    }

    private void Publish()
    {
        if (!dirty) return;
        long next = checked(version + 1);
        var frames = participants.Values.Where(participant => participant.Connection != null)
            .Select(participant => (Participant: participant, Captured: Capture(participant, participant.Modules,
                participant.PublishedModules, false, next, participant.ConnectionVersion))).ToArray();
        version = next;
        dirty = false;
        foreach (var (participant, captured) in frames)
        {
            participant.PublicationSequence = captured.Frame.PublicationSequence;
            participant.PublishedModules = captured.Modules;
            participant.Transients.Clear();
            if (participant.Acknowledged)
            {
                participant.Results.Clear();
                FramePublished?.Invoke(participant.Connection!, captured.Frame);
            }
            else if (participant.BufferedFrames.Count < 128)
            {
                participant.Results.Clear();
                participant.BufferedFrames.Enqueue(captured.Frame);
            }
            else Prepare(participant);
        }
    }

    private Captured Capture(
        Participant participant, Dictionary<string, ISessionObservationModule> selected,
        Dictionary<string, string?> previous, bool full, long stateVersion, long generation)
    {
        var states = new Dictionary<string, string?>(previous, StringComparer.Ordinal);
        var updates = new List<ModuleSnapshot>();
        foreach (ISessionObservationModule module in selected.Values)
        {
            previous.TryGetValue(module.Id, out string? published);
            ModuleProjection projection = participant.UnitId == null && module.RequiresControlledUnit
                ? new(ModuleProjectionKind.Removed) : module.Capture(participant.UnitId, stateVersion, full, published);
            if (full && projection.Kind is not (ModuleProjectionKind.Full or ModuleProjectionKind.Removed) ||
                projection.Kind == ModuleProjectionKind.Delta && (published == null || projection.PublishedState == null) ||
                !Enum.IsDefined(projection.Kind))
                throw new InvalidOperationException("An observation module produced an incomplete or invalid projection.");
            if (projection.Kind == ModuleProjectionKind.Unchanged) continue;
            ModuleUpdateKind kind = projection.Kind switch
            {
                ModuleProjectionKind.Full => ModuleUpdateKind.Full,
                ModuleProjectionKind.Delta => ModuleUpdateKind.Delta,
                ModuleProjectionKind.Removed => ModuleUpdateKind.Removed,
                _ => throw new InvalidOperationException("The module projection kind is invalid.")
            };
            if (kind == ModuleUpdateKind.Removed) states.Remove(module.Id);
            else states[module.Id] = projection.PublishedState ?? projection.Payload;
            updates.Add(new() { Id = module.Id, Version = module.Version, StateVersion = stateVersion,
                Kind = kind, Payload = kind == ModuleUpdateKind.Removed ? "" : projection.Payload });
        }
        return new(new()
        {
            StateVersion = stateVersion, PublicationSequence = checked(participant.PublicationSequence + 1),
            ConnectionVersion = generation, ControlledUnitId = participant.UnitId,
            IsFull = full,
            World = simulation.CaptureWorld(participant.UnitId) with { IsCompleted = simulation.IsCompleted },
            Modules = Array.AsReadOnly(updates.ToArray()), Events = CaptureEvents(participant, full)
        }, states);
    }

    private static IReadOnlyList<EventEnvelope> CaptureEvents(Participant participant, bool full)
    {
        if (!full) return Array.AsReadOnly(participant.Results.Concat(participant.Transients).ToArray());
        var retained = new List<EventEnvelope>();
        if (!participant.Acknowledged && participant.Prepared is { } prepared)
            retained.AddRange(prepared.Frame.Events);
        foreach (ObservationFrame buffered in participant.BufferedFrames) retained.AddRange(buffered.Events);
        retained.AddRange(participant.Results);
        return Array.AsReadOnly(retained.ToArray());
    }

    private Participant Bound(string connection, long generation)
    {
        if (string.IsNullOrWhiteSpace(connection) ||
            !connections.TryGetValue(connection, out Participant? participant) || participant.ConnectionVersion != generation)
            throw Fault(SessionRequestFailure.Unauthorized, "The connection generation is no longer authorized.");
        return participant;
    }

    private Dictionary<string, ISessionObservationModule> SelectModules(IReadOnlyList<ModuleVersion> requested)
    {
        if (requested == null) throw Fault(SessionRequestFailure.InvalidRequest, "Module capabilities are required.");
        var selected = new Dictionary<string, ISessionObservationModule>(StringComparer.Ordinal);
        foreach (ModuleVersion contract in requested)
            if (contract == null || string.IsNullOrWhiteSpace(contract.Id) ||
                !modules.TryGetValue(contract.Id, out ISessionObservationModule? module) ||
                module.Version != contract.Version || !selected.TryAdd(contract.Id, module))
                throw Fault(SessionRequestFailure.UnsupportedModule, "An observation module is unknown, duplicated or version-incompatible.");
        return selected;
    }

    private SessionBinding Binding(Participant participant) => new(Id, participant.Id, participant.UnitId, participant.ConnectionVersion);
    private static SessionRequestException Fault(SessionRequestFailure failure, string message) => new(failure, message);
    private static void ValidateProtocol(int requested)
    {
        if (requested != SessionProtocol.Version)
            throw Fault(SessionRequestFailure.UnsupportedProtocol, "The session protocol version is unsupported.");
    }
    private static bool TokenMatches(string expected, string supplied)
    {
        if (supplied == null || supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied));
    }

    private async Task<T> Call<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Request<T>(action, cancellationToken);
        if (!requests.Writer.TryWrite(request))
            throw Fault(SessionRequestFailure.Unavailable, "The session request queue is full or closed.");
        context.Inbox.Signal();
        await Task.WhenAny(request.Response.Task, loop).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (loop.IsFaulted) await loop.ConfigureAwait(false);
        if (request.Response.Task.IsCompleted) return await request.Response.Task.ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        throw Fault(SessionRequestFailure.Unavailable, "The session execution owner has stopped.");
    }

    public ValueTask DisposeAsync() => new(disposal.Value);

    private async Task DisposeCoreAsync()
    {
        stopping.Cancel();
        context.Inbox.Signal();
        try { await loop.ConfigureAwait(false); }
        finally { context.Dispose(); stopping.Dispose(); }
    }
}
