using Miaosoft.Sim.Client;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Runtime.EventData;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

namespace Miaosoft.Sim.Samples.NetworkSession;

public sealed partial class NetworkSessionSample
{
    [TestMethod]
    public async Task RealNetworkCompletionReportsInterruptedOutcomeAndDoesNotResumeOldCombatDuringRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        UnitSnapshot source = Self(first), target = Self(second);
        AbilitySnapshot ability = source.Abilities.Single(candidate => candidate.DefinitionId == "moving-bolt");
        OperationReceipt accepted = await first.SendAsync(new CastAbilityCommand
        {
            UnitId = source.Id, MapInstanceId = source.MapInstanceId!.Value,
            ControlVersion = source.ControlVersion, AbilityInstanceId = ability.RuntimeInstanceId, TargetUnitId = target.Id
        }, token);
        Assert.AreEqual(OperationQueueStatus.Enqueued, accepted.Status);
        await host.Session.RequestCompletionAsync(token);
        await WaitUntil(() => first.World.State.World.IsCompleted && first.World.State.World.TimeScale == 0 &&
            second.World.State.World.IsCompleted, token);
        OperationResult interrupted = await first.WaitForOutcomeAsync(accepted.Sequence, token);
        Assert.AreEqual(OperationExecutionStatus.Unknown, interrupted.Status);
        OperationReceipt denied = await first.SendAsync(Stop(first), token);
        Assert.AreEqual(OperationQueueStatus.Completed, denied.Status);
        Assert.AreEqual(accepted.Sequence + 1, denied.NextOperationSequence);
        await first.ClearUnqueuedPendingAsync(token);
        PreparedSessionSnapshot before = await first.RefreshSnapshotAsync(token);
        var isolated = new ClientWorld(EventCodec.CreateBuilder().Build());
        isolated.Prepare(before);
        ClientObservationState frozen = isolated.State;
        var resumed = before.Frame with
        {
            IsFull = false, PublicationSequence = before.Frame.PublicationSequence + 1,
            StateVersion = before.Frame.StateVersion + 1, World = before.Frame.World with { IsCompleted = false }
        };
        Assert.ThrowsExactly<InvalidDataException>(() => isolated.Apply(resumed));
        Assert.AreSame(frozen, isolated.State);
        Assert.ThrowsExactly<InvalidDataException>(() => isolated.Prepare(
            before with { Frame = before.Frame with { World = before.Frame.World with { IsCompleted = false } } }));
        Assert.AreSame(frozen, isolated.State);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
        PreparedSessionSnapshot after = await first.RejoinAsync(cancellationToken: token);
        Assert.IsTrue(after.Frame.World.IsCompleted);
        Assert.AreEqual(before.Frame.World.Time, after.Frame.World.Time);
        Assert.AreEqual(0, after.Frame.World.TimeScale);
        Assert.AreEqual(source.Energy, Self(first).Energy);
        Assert.AreEqual(target.Life, Self(second).Life);
        Assert.HasCount(0, after.Frame.World.Projectiles);
        Assert.AreEqual(accepted.Sequence + 1, after.NextOperationSequence);
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine("Completion settled the timestamp, froze simulation and discarded pending windup. The accepted operation produced explicit Unknown, not a false business rejection or success. Subsequent combat was refused without consuming its number; snapshot/rejoin beyond the old release deadline did not debit energy, launch or hit.");
    }

    [TestMethod]
    public async Task SessionResultRecordsAreCreatedAfterExecutionAndRemainAvailableAfterCompletion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        var (simulation, units, _) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token);
        var recorded = simulation.Context.Factory.NewEvent<RecordedResultEvent>() with { Receiver = units[0].EventReceiver };
        Assert.IsNull(recorded.Execute);
        Assert.IsNotNull(recorded.ToString());
        simulation.Context.Dispatch(recorded).ToList();
        await using LoopbackSession host = await LoopbackSession.StartAsync(
            new SessionRuntime(simulation, EventCodec.CreateBuilder().Build(), enableDemoAdmission: true), token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        IReadOnlyList<SessionResultRecord> before = await host.Session.GetResultRecordsAsync(token);
        Assert.HasCount(1, before);
        Assert.AreEqual(recorded.Execute!.Value, before.Single().Time);
        await host.Session.RequestCompletionAsync(token);
        await WaitUntil(() => first.World.State.World.IsCompleted, token);
        IReadOnlyList<SessionResultRecord> after = await host.Session.GetResultRecordsAsync(token);
        CollectionAssert.AreEqual(before.ToArray(), after.ToArray());
        TestContext.WriteLine("A result event could be printed before enqueue without executing projection. The owner created its detached typed result only after Execute was assigned and retained it after completion.");
    }

    private sealed record RecordedResult(TimeSpan Time) : SessionResultRecord(Time);
    private sealed record RecordedResultEvent : Event, ISessionResultRecordEvent
    {
        public SessionResultRecord CreateResultRecord() => new RecordedResult(
            Execute ?? throw new InvalidOperationException("The result has not executed."));
    }
}
