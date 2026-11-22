using Miaosoft.Sim.Client;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;
using Miaosoft.Sim.Protocol.Event;
using System.Collections.Concurrent;

namespace Miaosoft.Sim.Samples.NetworkSession;

[TestClass]
public sealed partial class NetworkSessionSample
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RealLoopbackRunsTheSharedCombatChainAndRejoinsTheSameAuthoritativeParticipant()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        var facts = new ConcurrentQueue<ProjectileFact>();
        first.ObservationChanged += state =>
        {
            foreach (ProjectileFact fact in state.Events.OfType<ProjectileFact>()) facts.Enqueue(fact);
        };
        SessionWelcome one = await first.EnterDemoAsync(host.Address, token);
        SessionWelcome two = await second.EnterDemoAsync(host.Address, token);
        Assert.AreNotEqual(one.Credentials.ParticipantId, two.Credentials.ParticipantId);
        TestContext.WriteLine($"Prepared participants at scales {first.World.State.World.TimeScale}/{second.World.State.World.TimeScale}; waiting for the committed start frame.");
        await WaitUntil(() => first.World.State.World.TimeScale == 1 && second.World.State.World.TimeScale == 1, token);
        Guid sourceId = one.Snapshot.Binding.UnitId!.Value;
        Guid targetId = two.Snapshot.Binding.UnitId!.Value;
        Guid mapId = first.World.State.World.Maps.Single().InstanceId;
        Assert.AreEqual(Self(first).MapInstanceId, mapId);
        Assert.IsNotNull(Self(first).Experience);
        Assert.IsNull(first.World.State.World.Units.Single(unit => unit.Id == targetId).Experience);
        int sourceLife = Self(first).Life;
        int targetLife = Self(second).Life;
        Assert.HasCount(2, Self(first).Abilities);
        for (int hit = 0; hit < 2; hit++)
        {
            UnitSnapshot source = Self(first);
            AbilitySnapshot ability = source.Abilities.Single(candidate => candidate.DefinitionId == "bolt");
            int expectedDamage = ability.Level * 10 + source.Level;
            int expectedHeal = 7;
            OperationReceipt receipt = await first.SendAsync(new CastAbilityCommand
            {
                UnitId = source.Id, MapInstanceId = source.MapInstanceId!.Value,
                ControlVersion = source.ControlVersion, AbilityInstanceId = ability.RuntimeInstanceId, TargetUnitId = targetId
            }, token);
            Assert.AreEqual(OperationQueueStatus.Enqueued, receipt.Status);
            OperationResult result = await first.WaitForOutcomeAsync(receipt.Sequence, token);
            Assert.AreEqual(OperationExecutionStatus.Succeeded, result.Status, result.Reason);
            sourceLife += expectedHeal;
            targetLife -= expectedDamage;
            await WaitUntil(() => Self(first).Life == sourceLife && Self(second).Life == targetLife, token);
            await WaitUntil(() => facts.Count(fact => fact.AbilityId == "bolt" && fact.Kind == ProjectileFactKind.Hit) == hit + 1, token);
            Assert.AreEqual(hit + 1, facts.Count(fact => fact.AbilityId == "bolt" && fact.Kind == ProjectileFactKind.Emitted));
            Assert.AreEqual(targetLife, first.World.State.World.Units.Single(unit => unit.Id == targetId).Life);
            Assert.AreEqual(sourceLife, second.World.State.World.Units.Single(unit => unit.Id == sourceId).Life);
            StatusSnapshot applied = Self(second).Statuses.Single();
            Assert.AreEqual("fortify", applied.DefinitionId);
            Assert.AreEqual(hit + 1, applied.Stacks);
            Assert.AreEqual(sourceId, applied.SourceUnitId);
            Assert.IsNotNull(applied.ExpiresAt);
            Assert.IsGreaterThan(second.World.State.World.Time, applied.ExpiresAt.Value);
            TestContext.WriteLine($"hit {hit + 1}: accepted operation {receipt.Sequence}, actual damage {expectedDamage}, independent heal {expectedHeal}, life {sourceLife}/{targetLife}, status stacks {applied.Stacks}, committed expiry {applied.ExpiresAt}.");
        }
        UnitSnapshot moving = Self(first);
        OperationReceipt move = await first.SendAsync(new MoveCommand
        {
            UnitId = sourceId, MapInstanceId = moving.MapInstanceId!.Value, ControlVersion = moving.ControlVersion,
            Destination = new(14, 10)
        }, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(move.Sequence, token)).Status);
        await WaitUntil(() => Self(first).Position.X > 10, token);
        OperationReceipt retarget = await first.SendAsync(new MoveCommand
        {
            UnitId = sourceId, MapInstanceId = moving.MapInstanceId.Value, ControlVersion = moving.ControlVersion,
            Destination = new(16, 10)
        }, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(retarget.Sequence, token)).Status);
        OperationReceipt stop = await first.SendAsync(new StopMoveCommand
        {
            UnitId = sourceId, MapInstanceId = moving.MapInstanceId.Value, ControlVersion = moving.ControlVersion
        }, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(stop.Sequence, token)).Status);
        SessionBinding old = first.World.State.Binding;
        StatusSnapshot beforeRejoin = Self(second).Statuses.Single();
        await first.DisconnectAsync(token);
        PreparedSessionSnapshot rejoined = await first.RejoinAsync(cancellationToken: token);
        Assert.IsFalse(rejoined.Frame.Events.Select(envelope => EventCodec.CreateBuilder().Build()
            .Decode(envelope, EventDirection.ServerToClient)).OfType<ITransientEventMessage>().Any());
        Assert.AreEqual(old.ParticipantId, rejoined.Binding.ParticipantId);
        Assert.AreEqual(sourceId, rejoined.Binding.UnitId);
        Assert.AreEqual(mapId, first.World.State.World.Maps.Single().InstanceId);
        Assert.IsGreaterThan(old.ConnectionVersion, rejoined.Binding.ConnectionVersion);
        Assert.AreEqual(stop.Sequence + 1, rejoined.NextOperationSequence);
        Assert.AreEqual(sourceLife, Self(first).Life);
        Assert.AreEqual(targetLife, first.World.State.World.Units.Single(unit => unit.Id == targetId).Life);
        StatusSnapshot restored = first.World.State.World.Units.Single(unit => unit.Id == targetId).Statuses.Single();
        Assert.AreEqual(beforeRejoin.RuntimeInstanceId, restored.RuntimeInstanceId);
        Assert.AreEqual(beforeRejoin.ApplicationId, restored.ApplicationId);
        Assert.AreEqual(beforeRejoin.ExpiresAt, restored.ExpiresAt);
        OperationReceipt after = await first.SendAsync(new StopMoveCommand
        {
            UnitId = sourceId, MapInstanceId = Self(first).MapInstanceId!.Value,
            ControlVersion = Self(first).ControlVersion
        }, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(after.Sequence, token)).Status);
        Assert.IsGreaterThan(rejoined.Frame.StateVersion, first.World.State.StateVersion);
        Assert.IsNull(first.ObservationFailure);
        Assert.IsNull(second.ObservationFailure);
        await WaitUntil(() => Self(second).Statuses.Count == 0 &&
            first.World.State.World.Units.Single(unit => unit.Id == targetId).Statuses.Count == 0, token);
        Assert.AreEqual(100, Self(second).MaximumLife);
        TestContext.WriteLine("Map instance and owner-only experience survived rejoin; the same status runtime/application/expiry identity was restored, then both clients removed it and observed the maximum-life projection return to 100.");
        TestContext.WriteLine("Both clients used real loopback SignalR. Retarget and stop received actual domain results; reconnect retained identity, state and operation numbering before subsequent authoritative updates.");
    }

    private static UnitSnapshot Self(GameClient client) => client.World.State.World.Units.Single(
        unit => unit.Id == client.World.State.Binding.UnitId);

    private static async Task WaitUntil(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }
}
