using Microsoft.AspNetCore.SignalR;
using Miaosoft.Sim.Client;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

namespace Miaosoft.Sim.Samples.NetworkSession;

public sealed partial class NetworkSessionSample
{
    [TestMethod]
    public async Task RealNetworkRetainsExactly1024OriginalQueueReceiptsWithoutReexecutingRetries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        var codec = EventCodec.CreateBuilder().Build();
        EventEnvelope original = codec.Encode(Stop(first), EventDirection.ClientToServer);
        OperationReceipt? firstReceipt = null;
        int firstOutcomes = 0;
        first.ObservationChanged += state => Interlocked.Add(ref firstOutcomes,
            state.Events.OfType<OperationResult>().Count(outcome => outcome.OperationSequence == 1));
        for (long start = 1; start <= 1024; start += 8)
        {
            ParticipantOperation[] operations = Enumerable.Range(0, 8)
                .Select(index => new ParticipantOperation(start + index, original)).ToArray();
            IReadOnlyList<OperationReceipt> receipts = await first.SubmitBatchAsync(Batch(first, operations), token);
            Assert.IsTrue(receipts.All(receipt => receipt.Status == OperationQueueStatus.Enqueued));
            firstReceipt ??= receipts[0];
            await first.WaitForOutcomeAsync(start + 7, token);
        }
        IReadOnlyList<OperationReceipt> retained = await first.SubmitBatchAsync(
            Batch(first, new ParticipantOperation(1, original)), token);
        Assert.AreEqual(firstReceipt, retained.Single());
        Assert.AreEqual(1, Volatile.Read(ref firstOutcomes));
        OperationReceipt last = (await first.SubmitBatchAsync(Batch(first, new ParticipantOperation(1025, original)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.Enqueued, last.Status);
        await first.WaitForOutcomeAsync(1025, token);
        OperationReceipt expired = (await first.SubmitBatchAsync(Batch(first, new ParticipantOperation(1, original)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.Expired, expired.Status);
        Assert.AreEqual(1026L, expired.NextOperationSequence);
        PreparedSessionSnapshot rejoin = await first.RejoinAsync(cancellationToken: token);
        Assert.AreEqual(1026L, rejoin.NextOperationSequence);
        OperationReceipt other = await second.SendAsync(Stop(second), token);
        Assert.AreEqual(1L, other.Sequence);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await second.WaitForOutcomeAsync(1, token)).Status);
        TestContext.WriteLine("Receipt 1 remained identical at high-water 1024 and expired immediately after 1025. Its business outcome occurred once; reused correlation IDs did not merge operations, rejoin retained 1026 and the other participant still began at one.");
    }

    [TestMethod]
    public async Task RealNetworkReportsMixedBatchQueuePressureConflictAndGapWithoutConsumingRejectedNumbers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token, inputCapacity: 1);
        await using var first = new GameClient();
        await using var second = new GameClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        var codec = EventCodec.CreateBuilder().Build();
        EventEnvelope original = codec.Encode(Stop(first), EventDirection.ClientToServer);
        IReadOnlyList<OperationReceipt> receipts = await first.SubmitBatchAsync(
            Batch(first, new(1, original), new(2, original)), token);
        Assert.AreEqual(OperationQueueStatus.Enqueued, receipts[0].Status);
        Assert.AreEqual(OperationQueueStatus.QueueFull, receipts[1].Status);
        Assert.AreEqual(2L, receipts[1].NextOperationSequence);
        await first.WaitForOutcomeAsync(1, token);
        UnitSnapshot self = Self(first);
        EventEnvelope changed = codec.Encode(new MoveCommand
        {
            UnitId = self.Id, MapInstanceId = self.MapInstanceId!.Value,
            ControlVersion = self.ControlVersion, Destination = new(12, 10)
        }, EventDirection.ClientToServer) with { CorrelationId = original.CorrelationId };
        OperationReceipt conflict = (await first.SubmitBatchAsync(Batch(first, new ParticipantOperation(1, changed)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.SequenceConflict, conflict.Status);
        IReadOnlyList<OperationReceipt> retry = await first.SubmitBatchAsync(
            Batch(first, new(1, original), new(2, original), new(3, original)), token);
        Assert.AreEqual(receipts[0], retry[0]);
        Assert.AreEqual(OperationQueueStatus.Enqueued, retry[1].Status);
        Assert.AreEqual(OperationQueueStatus.QueueFull, retry[2].Status);
        await first.WaitForOutcomeAsync(2, token);
        IReadOnlyList<OperationReceipt> invalid = await first.SubmitBatchAsync(
            Batch(first, new(4, original), new(0, original), new(long.MaxValue, original)), token);
        CollectionAssert.AreEqual(new[] { OperationQueueStatus.SequenceGap, OperationQueueStatus.Invalid, OperationQueueStatus.Invalid },
            invalid.Select(receipt => receipt.Status).ToArray());
        Assert.IsTrue(invalid.All(receipt => receipt.NextOperationSequence == 3));
        OperationReceipt next = (await first.SubmitBatchAsync(Batch(first, new ParticipantOperation(3, original)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.Enqueued, next.Status);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(3, token)).Status);
        TestContext.WriteLine("A one-operation capacity produced exact per-item receipts, including a mixed replay/new/full batch. Conflicts, gaps, nonpositive and overflow-reserved sequences consumed no new number; the expected third operation still executed.");
    }

    [TestMethod]
    public async Task RealNetworkRejectsOldConnectionsForeignUnitsAndCrossSessionCredentialsBeforeSequenceAccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token);
        await using LoopbackSession foreign = await LoopbackSession.StartAsync(token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        await using var replacement = new GameClient();
        await using var intruder = new GameClient();
        SessionWelcome original = await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        var codec = EventCodec.CreateBuilder().Build();
        EventEnvelope stop = codec.Encode(Stop(first), EventDirection.ClientToServer);
        EventBatch old = Batch(first, new ParticipantOperation(1, stop));
        PreparedSessionSnapshot snapshot = await replacement.RejoinAsync(host.Address, original.Credentials, token);
        Assert.IsGreaterThan(original.Snapshot.Binding.ConnectionVersion, snapshot.Binding.ConnectionVersion);
        await Assert.ThrowsExactlyAsync<HubException>(() => first.SubmitBatchAsync(old, token));
        await Assert.ThrowsExactlyAsync<HubException>(() => intruder.RejoinAsync(foreign.Address, original.Credentials, token));
        EventEnvelope unauthorized = codec.Encode(Stop(second), EventDirection.ClientToServer);
        OperationReceipt denied = (await replacement.SubmitBatchAsync(Batch(replacement, new ParticipantOperation(1, unauthorized)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.Unauthorized, denied.Status);
        Assert.IsNull(denied.NextOperationSequence);
        OperationReceipt accepted = await replacement.SendAsync(Stop(replacement), token);
        Assert.AreEqual(1L, accepted.Sequence);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await replacement.WaitForOutcomeAsync(1, token)).Status);
        TestContext.WriteLine("A fresh authenticated connection invalidated the still-open old connection. Credentials could not cross sessions, foreign-unit input revealed no sequence hint, and the valid replacement's first operation remained one.");
    }

    private static StopMoveCommand Stop(GameClient client)
    {
        UnitSnapshot self = Self(client);
        return new() { UnitId = self.Id, MapInstanceId = self.MapInstanceId!.Value, ControlVersion = self.ControlVersion };
    }

    private static EventBatch Batch(GameClient client, params ParticipantOperation[] operations) => new()
    {
        ParticipantId = client.World.State.Binding.ParticipantId,
        ConnectionVersion = client.World.State.Binding.ConnectionVersion, Operations = operations
    };
}
