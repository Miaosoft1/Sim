using Microsoft.AspNetCore.SignalR;
using Miaosoft.Sim.Client;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

namespace Miaosoft.Sim.Samples.NetworkSession;

public sealed partial class NetworkSessionSample
{
    [TestMethod]
    public async Task RealNetworkPublishesAtomicModuleDeltasClearsRevokedPrivateStateAndPreservesRejoinContracts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        var serverModule = new CounterModule();
        await using LoopbackSession host = await LoopbackSession.StartAsync(token, [serverModule]);
        await using var first = new GameClient(observationModules: [new CounterModule()]);
        await using var second = new GameClient(observationModules: [new CounterModule()]);
        await using var missing = new GameClient();
        await using var incompatible = new GameClient(observationModules: [new CounterModule(2)]);
        SessionWelcome welcome = await first.EnterDemoAsync(host.Address, token);
        var isolated = new ClientWorld(EventCodec.CreateBuilder().Build(), [new CounterModule()]);
        isolated.Prepare(welcome.Snapshot);
        ClientObservationState beforeInvalid = isolated.State;
        long invalidVersion = welcome.Snapshot.Frame.StateVersion + 1;
        var invalid = welcome.Snapshot.Frame with
        {
            IsFull = false, StateVersion = invalidVersion,
            PublicationSequence = welcome.Snapshot.Frame.PublicationSequence + 1,
            World = welcome.Snapshot.Frame.World with
            {
                Units = welcome.Snapshot.Frame.World.Units.Select(unit => unit with { Life = 1 }).ToArray()
            },
            Modules = [new()
            {
                Id = serverModule.Id, Version = 2, StateVersion = invalidVersion,
                Kind = ModuleUpdateKind.Delta, Payload = "1"
            }]
        };
        Assert.ThrowsExactly<InvalidDataException>(() => isolated.Apply(invalid));
        Assert.AreSame(beforeInvalid, isolated.State);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        ClientObservationState initial = first.World.State;
        object originalState = initial.Modules[serverModule.Id];
        Assert.AreEqual(initial.StateVersion, CounterModule.Value(originalState));
        EventEnvelope original = EventCodec.CreateBuilder().Build().Encode(Stop(first), EventDirection.ClientToServer);
        OperationReceipt accepted = (await first.SubmitBatchAsync(
            Batch(first, new ParticipantOperation(1, original)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.Enqueued, accepted.Status);
        await first.WaitForOutcomeAsync(1, token);
        ClientObservationState updated = first.World.State;
        Assert.IsGreaterThan(initial.StateVersion, updated.StateVersion);
        Assert.AreEqual(updated.StateVersion, CounterModule.Value(updated.Modules[serverModule.Id]));
        Assert.AreEqual(initial.StateVersion, CounterModule.Value(originalState));
        await Assert.ThrowsExactlyAsync<HubException>(() => missing.RejoinAsync(host.Address, welcome.Credentials, token));
        await Assert.ThrowsExactlyAsync<HubException>(() => incompatible.RejoinAsync(host.Address, welcome.Credentials, token));
        Assert.AreEqual(welcome.Snapshot.Binding.ConnectionVersion, first.World.State.Binding.ConnectionVersion);
        Guid unitId = welcome.Snapshot.Binding.UnitId!.Value;
        await host.Session.SetControlledUnitAsync(welcome.Credentials.ParticipantId, null, token);
        await WaitUntil(() => first.World.State.Binding.UnitId == null, token);
        Assert.IsFalse(first.World.State.Modules.ContainsKey(serverModule.Id));
        Assert.AreEqual(0, serverModule.UnitlessCaptures);
        OperationReceipt revoked = (await first.SubmitBatchAsync(
            Batch(first, new ParticipantOperation(1, original)), token)).Single();
        Assert.AreEqual(OperationQueueStatus.Unauthorized, revoked.Status);
        Assert.IsNull(revoked.NextOperationSequence);
        await host.Session.SetControlledUnitAsync(welcome.Credentials.ParticipantId, unitId, token);
        await WaitUntil(() => first.World.State.Binding.UnitId == unitId &&
            first.World.State.Modules.ContainsKey(serverModule.Id), token);
        Assert.AreEqual(first.World.State.StateVersion, CounterModule.Value(first.World.State.Modules[serverModule.Id]));
        OperationReceipt next = await first.SendAsync(Stop(first), token);
        Assert.AreEqual(2L, next.Sequence);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(next.Sequence, token)).Status);
        PreparedSessionSnapshot rejoined = await first.RejoinAsync(cancellationToken: token);
        Assert.AreEqual(rejoined.Frame.StateVersion, CounterModule.Value(first.World.State.Modules[serverModule.Id]));
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine("Full and delta module values committed at the world's exact state version without mutating old client state. Missing/version-mismatched rejoin contracts were rejected before rebinding. Revoking control forced explicit private-state removal without invoking the custom projector or exposing operation progress; restoration and full rejoin retained the module.");
    }

    [TestMethod]
    public async Task RealNetworkRequiresExplicitDemoAdmissionOptIn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using LoopbackSession host = await LoopbackSession.StartAsync(timeout.Token, enableDemo: false);
        await using var client = new GameClient();
        HubException error = await Assert.ThrowsExactlyAsync<HubException>(() => client.EnterDemoAsync(host.Address, timeout.Token));
        StringAssert.Contains(error.Message, "DemoDisabled");
        TestContext.WriteLine("Binding a loopback listener did not implicitly enable anonymous demonstration admission.");
    }
}
