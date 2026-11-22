using Miaosoft.Sim.Client;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Hub;
using Microsoft.AspNetCore.SignalR.Client;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;
using System.Collections.Concurrent;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Samples.NetworkSession;

public sealed partial class NetworkSessionSample
{
    [TestMethod]
    [DataRow(ProjectileExplosionTrigger.OnHit)]
    [DataRow(ProjectileExplosionTrigger.OnArrival)]
    public async Task ConfirmedExplosionFactsAreLiveOnlyAndNeverSentToAUnitlessRecipient(ProjectileExplosionTrigger trigger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        var (simulation, units, _) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token);
        units[0].EventReceiver.GetHandlers<ProjectileAbility>().Single(ability => ability.Id == "bolt").ExplosionTrigger = trigger;
        var session = new SessionRuntime(simulation, EventCodec.CreateBuilder().Build(), enableDemoAdmission: true);
        await using LoopbackSession host = await LoopbackSession.StartAsync(session, token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        var facts = new ConcurrentQueue<ProjectileFact>();
        var unitlessFacts = new ConcurrentQueue<ProjectileFact>();
        first.ObservationChanged += state =>
        {
            foreach (ProjectileFact fact in state.Events.OfType<ProjectileFact>()) facts.Enqueue(fact);
        };
        second.ObservationChanged += state =>
        {
            foreach (ProjectileFact fact in state.Events.OfType<ProjectileFact>()) unitlessFacts.Enqueue(fact);
        };
        await first.EnterDemoAsync(host.Address, token);
        SessionWelcome other = await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        UnitSnapshot source = Self(first), target = Self(second);
        AbilitySnapshot ability = source.Abilities.Single(candidate => candidate.DefinitionId == "bolt");
        int expectedLife = target.Life - ability.Level * 10 - source.Level;
        await session.SetControlledUnitAsync(other.Credentials.ParticipantId, null, token);
        await WaitUntil(() => second.World.State.Binding.UnitId == null, token);
        OperationReceipt accepted = await first.SendAsync(new CastAbilityCommand
        {
            UnitId = source.Id, MapInstanceId = source.MapInstanceId!.Value, ControlVersion = source.ControlVersion,
            AbilityInstanceId = ability.RuntimeInstanceId, TargetUnitId = target.Id
        }, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(accepted.Sequence, token)).Status);
        await WaitUntil(() => facts.Count(fact => fact.AbilityId == "bolt") == 3 &&
            first.World.State.World.Units.Single(unit => unit.Id == target.Id).Life == expectedLife, token);
        ProjectileFact[] actual = facts.Where(fact => fact.AbilityId == "bolt").ToArray();
        CollectionAssert.AreEquivalent(
            new[] { ProjectileFactKind.Emitted, ProjectileFactKind.Hit, ProjectileFactKind.Explosion },
            actual.Select(fact => fact.Kind).ToArray());
        Assert.HasCount(1, actual.Select(fact => fact.ProjectileId).Distinct().ToArray());
        Assert.IsTrue(actual.All(fact => fact.AbilityInstanceId == ability.RuntimeInstanceId));
        Assert.AreEqual(target.Position, actual.Single(fact => fact.Kind == ProjectileFactKind.Explosion).Position);
        Assert.HasCount(0, unitlessFacts);
        PreparedSessionSnapshot refreshed = await first.RefreshSnapshotAsync(token);
        EventCodecRegistry codec = EventCodec.CreateBuilder().Build();
        Assert.IsFalse(refreshed.Frame.Events.Select(envelope => codec.Decode(envelope, EventDirection.ServerToClient))
            .OfType<ITransientEventMessage>().Any());
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine($"{trigger}: one actual emission, semantic hit and explosion retained the original projectile/ability identity and confirmed impact position. Damage settled once; a unitless recipient received no facts and full refresh replayed none.");
    }

    [TestMethod]
    public async Task RealNetworkRejoinsAnExistingFlightWithoutConfusingActivationWithItsLaterHit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        var facts = new ConcurrentQueue<ProjectileFact>();
        first.ObservationChanged += state =>
        {
            foreach (ProjectileFact fact in state.Events.OfType<ProjectileFact>()) facts.Enqueue(fact);
        };
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        UnitSnapshot source = Self(first);
        UnitSnapshot target = Self(second);
        AbilitySnapshot ability = source.Abilities.Single(candidate => candidate.DefinitionId == "moving-bolt");
        int expectedDamage = ability.Level * 10 + source.Level;
        int expectedTargetLife = target.Life - expectedDamage;
        int expectedSourceLife = source.Life + 7;
        OperationReceipt queued = await first.SendAsync(new CastAbilityCommand
        {
            UnitId = source.Id, MapInstanceId = source.MapInstanceId!.Value,
            ControlVersion = source.ControlVersion, AbilityInstanceId = ability.RuntimeInstanceId, TargetUnitId = target.Id
        }, token);
        Assert.AreEqual(OperationQueueStatus.Enqueued, queued.Status);
        PreparedSessionSnapshot waiting = await first.RefreshSnapshotAsync(token);
        Assert.AreEqual(queued.Sequence + 1, waiting.NextOperationSequence);
        EventCodecRegistry codec = EventCodec.CreateBuilder().Build();
        Assert.IsFalse(waiting.Frame.Events.Select(envelope => codec.Decode(envelope, EventDirection.ServerToClient))
            .OfType<OperationResult>().Any(result => result.OperationSequence == queued.Sequence));
        Assert.HasCount(0, waiting.Frame.World.Projectiles);
        Assert.AreEqual(target.Life, waiting.Frame.World.Units.Single(unit => unit.Id == target.Id).Life);
        Assert.AreEqual(source.Energy, waiting.Frame.World.Units.Single(unit => unit.Id == source.Id).Energy);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(queued.Sequence, token)).Status);
        Assert.AreEqual(target.Life, Self(second).Life);
        await WaitUntil(() => first.World.State.World.Projectiles.Count != 0, token);
        ProjectileSnapshot flight = first.World.State.World.Projectiles.Single();
        await WaitUntil(() => facts.Any(fact => fact.ProjectileId == flight.Id && fact.Kind == ProjectileFactKind.Emitted), token);
        ProjectileFact emitted = facts.Single(fact => fact.ProjectileId == flight.Id && fact.Kind == ProjectileFactKind.Emitted);
        Assert.AreEqual(ability.RuntimeInstanceId, emitted.AbilityInstanceId);
        Assert.AreEqual(source.Position, emitted.Position);
        Assert.AreEqual(target.Position, emitted.Destination);
        Assert.AreEqual(ability.RuntimeInstanceId, flight.AbilityInstanceId);
        Assert.AreEqual(source.MapInstanceId, flight.MapInstanceId);
        await first.DisconnectAsync(token);
        PreparedSessionSnapshot rejoined = await first.RejoinAsync(cancellationToken: token);
        Assert.IsFalse(rejoined.Frame.Events.Select(envelope => codec.Decode(envelope, EventDirection.ServerToClient))
            .OfType<ITransientEventMessage>().Any());
        ProjectileSnapshot restored = rejoined.Frame.World.Projectiles.Single();
        Assert.AreEqual(flight.Id, restored.Id);
        Assert.AreEqual(flight.AbilityInstanceId, restored.AbilityInstanceId);
        Assert.AreEqual(flight.MapInstanceId, restored.MapInstanceId);
        Assert.AreEqual(target.Life, rejoined.Frame.World.Units.Single(unit => unit.Id == target.Id).Life);
        await WaitUntil(() => Self(second).Life == expectedTargetLife && Self(first).Life == expectedSourceLife &&
            first.World.State.World.Projectiles.Count == 0 && second.World.State.World.Projectiles.Count == 0, token);
        Assert.AreEqual(expectedDamage, target.Life - Self(second).Life);
        await WaitUntil(() => facts.Any(fact => fact.ProjectileId == flight.Id && fact.Kind == ProjectileFactKind.Hit), token);
        ProjectileFact impact = facts.Single(fact => fact.ProjectileId == flight.Id && fact.Kind == ProjectileFactKind.Hit);
        Assert.AreEqual(target.Position, impact.Position);
        Assert.AreEqual(target.Id, impact.TargetUnitId);
        Assert.HasCount(1, facts.Where(fact => fact.ProjectileId == flight.Id && fact.Kind == ProjectileFactKind.Emitted).ToArray());
        Assert.IsGreaterThan(emitted.EventSequence, impact.EventSequence);
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine($"A windup snapshot reported next accepted operation {waiting.NextOperationSequence} while showing no execution outcome, flight, resource debit or hit. Activation then completed before impact. Rejoin restored the same actual in-flight projectile and ability/map identities, then the later hit caused exactly {expectedDamage} damage and 7 independent healing; both clients removed the settled flight.");
    }

    [TestMethod]
    public async Task RealNetworkRepreparesOverflowedSnapshotsWithoutDroppingUnacknowledgedBusinessResults()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoopbackSession.StartAsync(token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        SessionWelcome welcome = await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        UnitSnapshot source = Self(first), target = Self(second);
        AbilitySnapshot ability = source.Abilities.Single(candidate => candidate.DefinitionId == "moving-bolt");
        int expectedLife = target.Life - ability.Level * 10 - source.Level;
        OperationReceipt accepted = await first.SendAsync(new CastAbilityCommand
        {
            UnitId = source.Id, MapInstanceId = source.MapInstanceId!.Value, ControlVersion = source.ControlVersion,
            AbilityInstanceId = ability.RuntimeInstanceId, TargetUnitId = target.Id
        }, token);
        await using HubConnection raw = new HubConnectionBuilder().WithUrl(host.Address).Build();
        raw.On<ObservationFrame>(nameof(ISessionHubClient.ReceiveFrame), _ => { });
        await raw.StartAsync(token);
        PreparedSessionSnapshot original = await raw.InvokeAsync<PreparedSessionSnapshot>(nameof(ISessionHubServer.Rejoin),
            new SessionRejoinRequest
            {
                ProtocolVersion = SessionProtocol.Version, Credentials = welcome.Credentials, Modules = []
            }, token);
        int publications = 0;
        second.ObservationChanged += _ => Interlocked.Increment(ref publications);
        await WaitUntil(() => Self(second).Life == expectedLife && second.World.State.World.Projectiles.Count == 0, token);
        Assert.IsGreaterThan(128, Volatile.Read(ref publications));
        SnapshotAcknowledgement stale = await raw.InvokeAsync<SnapshotAcknowledgement>(
            nameof(ISessionHubServer.AcknowledgeSnapshot),
            new SnapshotAcknowledgementRequest(original.Binding.ConnectionVersion, original.SnapshotId), token);
        Assert.AreEqual(SnapshotAcknowledgement.Stale, stale);
        PreparedSessionSnapshot current = await raw.InvokeAsync<PreparedSessionSnapshot>(
            nameof(ISessionHubServer.RequestSnapshot), original.Binding.ConnectionVersion, token);
        EventCodecRegistry codec = EventCodec.CreateBuilder().Build();
        OperationResult outcome = current.Frame.Events.Select(envelope => codec.Decode(envelope, EventDirection.ServerToClient))
            .OfType<OperationResult>().Single(result => result.OperationSequence == accepted.Sequence);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, outcome.Status, outcome.Reason);
        Assert.IsFalse(current.Frame.Events.Select(envelope => codec.Decode(envelope, EventDirection.ServerToClient))
            .OfType<ITransientEventMessage>().Any());
        Assert.AreEqual(expectedLife, current.Frame.World.Units.Single(unit => unit.Id == target.Id).Life);
        Assert.AreEqual(accepted.Sequence + 1, current.NextOperationSequence);
        SnapshotAcknowledgement ready = await raw.InvokeAsync<SnapshotAcknowledgement>(
            nameof(ISessionHubServer.AcknowledgeSnapshot),
            new SnapshotAcknowledgementRequest(current.Binding.ConnectionVersion, current.SnapshotId), token);
        Assert.AreEqual(SnapshotAcknowledgement.Started, ready);
        TestContext.WriteLine("More than 128 actual movement publications overflowed an unacknowledged full snapshot. Its old acknowledgement became stale; fresh preparation retained the single undelivered business result and the final world instead of treating an accepted sequence as execution proof.");
    }
}
