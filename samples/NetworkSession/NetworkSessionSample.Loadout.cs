using Miaosoft.Sim.Client;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Client;
using Miaosoft.Sim.Loadout.Protocol;
using Miaosoft.Sim.Loadout.Protocol.Command;
using Miaosoft.Sim.Loadout.Protocol.Model;
using Miaosoft.Sim.Loadout.Server;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Runtime.EventData;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

namespace Miaosoft.Sim.Samples.NetworkSession;

public sealed partial class NetworkSessionSample
{
    private static GameClient LoadoutClient() => new(
        EventCodec.CreateBuilder().AddLoadout().Build(), [new LoadoutClientModule()]);
    private static LoadoutSnapshot Loadout(GameClient client) => client.World.State.GetLoadout() ??
        throw new InvalidOperationException("The selected Loadout module is absent.");
    private static async Task<OperationResult> Execute(GameClient client, LoadoutCommand command, CancellationToken token)
    {
        OperationReceipt receipt = await client.SendAsync(command, token);
        Assert.AreEqual(OperationQueueStatus.Enqueued, receipt.Status, receipt.Reason);
        return await client.WaitForOutcomeAsync(receipt.Sequence, token);
    }

    [TestMethod]
    public async Task RealNetworkLoadoutKeepsOriginalItemAbilitiesExchangesVersionedConsentAndRoleOwnedTalents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoadoutArena.StartAsync(token);
        await using GameClient first = LoadoutClient(), second = LoadoutClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        LoadoutSnapshot original = Loadout(first), other = Loadout(second);
        ItemSnapshot firstItem = original.Inventory!.Items.Single(), secondItem = other.Inventory!.Items.Single();
        Assert.AreNotEqual(firstItem.InstanceId, secondItem.InstanceId);
        Assert.IsFalse(other.Inventory.Items.Any(item => item.InstanceId == firstItem.InstanceId));
        Assert.HasCount(0, original.Exchanges.Single().OtherItems);
        Assert.HasCount(0, other.Exchanges.Single().OtherItems);
        OperationReceipt foreign = await first.SendAsync(new InventoryCommand
        {
            UnitId = original.UnitId, ItemInstanceId = secondItem.InstanceId,
            Action = InventoryAction.Unequip, ExpectedRevision = other.Inventory.Revision
        }, token);
        Assert.AreEqual(OperationQueueStatus.Unauthorized, foreign.Status);
        Assert.IsNull(foreign.NextOperationSequence);
        await first.ClearUnqueuedPendingAsync(token);
        PreparedSessionSnapshot prepared = await first.RefreshSnapshotAsync(token);
        var isolated = new ClientWorld(EventCodec.CreateBuilder().AddLoadout().Build(), [new LoadoutClientModule()]);
        isolated.Prepare(prepared);
        ClientObservationState beforeInvalid = isolated.State;
        var invalid = prepared.Frame with
        {
            IsFull = false, StateVersion = prepared.Frame.StateVersion + 1,
            PublicationSequence = prepared.Frame.PublicationSequence + 1,
            Modules = [prepared.Frame.Modules.Single() with
            {
                StateVersion = prepared.Frame.StateVersion + 1,
                Payload = new LoadoutSnapshotCodec().Write(original with { UnitId = other.UnitId })
            }]
        };
        Assert.ThrowsExactly<InvalidDataException>(() => isolated.Apply(invalid));
        Assert.AreSame(beforeInvalid, isolated.State);

        TalentSnapshot tree = original.Talents.Single();
        var allocate = new TalentAllocateCommand
        {
            UnitId = original.UnitId, TreeId = tree.TreeId, NodeId = "practice",
            ExpectedRevision = tree.Revision, ExpectedAuthorityVersion = tree.AuthorityVersion
        };
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, allocate, token)).Status);
        await WaitUntil(() => Loadout(first).Talents.Single().AvailablePoints == 1, token);
        OperationResult stale = await Execute(first, allocate, token);
        Assert.AreEqual(OperationExecutionStatus.Rejected, stale.Status);
        Assert.AreEqual("StaleRevision", stale.Reason);
        Assert.AreEqual(2, tree.AvailablePoints);
        Assert.HasCount(0, tree.Ranks);

        var unequip = new InventoryCommand
        {
            UnitId = original.UnitId, ItemInstanceId = firstItem.InstanceId, Action = InventoryAction.Unequip,
            ExpectedRevision = original.Inventory.Revision
        };
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, unequip, token)).Status);
        await WaitUntil(() => Loadout(first).Inventory!.Items.Single().SlotId == null, token);
        Assert.AreEqual(OperationExecutionStatus.Rejected, (await Execute(first, unequip, token)).Status);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, unequip with
        {
            Action = InventoryAction.Equip, SlotId = "main", ExpectedRevision = Loadout(first).Inventory!.Revision
        }, token)).Status);
        await WaitUntil(() => Loadout(first).Inventory!.Items.Single().SlotId == "main", token);
        ExchangeSnapshot exchange = Loadout(first).Exchanges.Single();
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, new ExchangeCommand
        {
            UnitId = original.UnitId, ExchangeId = exchange.InstanceId, Action = ExchangeAction.Offer,
            ExpectedTermsRevision = 0, Offers = [new(firstItem.InstanceId, "main")]
        }, token)).Status);
        await WaitUntil(() => Loadout(second).Exchanges.Single().OtherItems.Count == 1, token);
        Assert.AreEqual(firstItem.InstanceId, Loadout(second).Exchanges.Single().OtherItems.Single().InstanceId);
        Assert.AreEqual(firstItem.DefinitionId, Loadout(second).Exchanges.Single().OtherItems.Single().DefinitionId);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(second, new ExchangeCommand
        {
            UnitId = other.UnitId, ExchangeId = exchange.InstanceId, Action = ExchangeAction.Offer,
            ExpectedTermsRevision = 1, Offers = [new(secondItem.InstanceId, "main")]
        }, token)).Status);
        var confirm = new ExchangeCommand
        {
            UnitId = original.UnitId, ExchangeId = exchange.InstanceId, Action = ExchangeAction.Confirm,
            ExpectedTermsRevision = 2
        };
        Assert.AreEqual(OperationExecutionStatus.Rejected, (await Execute(first, confirm with { ExpectedTermsRevision = 1 }, token)).Status);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, confirm, token)).Status);
        await WaitUntil(() => Loadout(first).Exchanges.Single().SelfConfirmed, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded,
            (await Execute(second, confirm with { UnitId = other.UnitId }, token)).Status);
        await WaitUntil(() => Loadout(first).Exchanges.Single().IsClosed && Loadout(second).Exchanges.Single().IsClosed, token);
        ItemSnapshot received = Loadout(first).Inventory!.Items.Single();
        Assert.AreEqual(secondItem.InstanceId, received.InstanceId);
        CollectionAssert.AreEqual(secondItem.AbilityInstanceIds.ToArray(), received.AbilityInstanceIds.ToArray());
        Assert.AreEqual(firstItem.InstanceId, Loadout(second).Inventory!.Items.Single().InstanceId);
        Assert.AreEqual(1, Loadout(first).Talents.Single().AvailablePoints);
        Assert.AreEqual(2, Loadout(second).Talents.Single().AvailablePoints);
        await first.RejoinAsync(cancellationToken: token);
        Assert.AreEqual(secondItem.InstanceId, Loadout(first).Inventory!.Items.Single().InstanceId);
        Assert.AreEqual(tree.InstanceId, Loadout(first).Talents.Single().InstanceId);
        await host.Session.SetControlledUnitAsync(first.Credentials!.ParticipantId, null, token);
        await WaitUntil(() => first.World.State.Binding.UnitId == null, token);
        Assert.IsNull(first.World.State.GetLoadout());
        await host.Session.SetControlledUnitAsync(first.Credentials.ParticipantId, original.UnitId, token);
        await WaitUntil(() => first.World.State.Binding.UnitId == original.UnitId &&
            first.World.State.GetLoadout() != null, token);
        Assert.AreEqual(secondItem.InstanceId, Loadout(first).Inventory!.Items.Single().InstanceId);
        Assert.IsNull(first.ObservationFailure);
        Assert.IsNull(second.ObservationFailure);
        TestContext.WriteLine("The optional module projected only each owner's inventory. Foreign item input revealed no sequence hint, a wrong-owner payload failed before atomic client publication, and revoking control removed the module. Real unequip/equip retained original ability IDs; stale revisions and consent terms rejected. Bilateral consent exchanged the original item and ability identities while talent instances/points remained with their roles; full rejoin retained both.");
    }

    [TestMethod]
    public async Task RealNetworkCompletedSessionOnlyAcceptsExistingAuthorizedDeathInspectionAndClaimWithoutResumingCombat()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoadoutArena.StartAsync(token, existingDeath: true);
        await using GameClient first = LoadoutClient(), second = LoadoutClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        LoadoutSnapshot initial = Loadout(first);
        DeathInspectionSnapshot authorization = initial.DeathInspections.Single();
        Assert.AreEqual("NotInspected", authorization.Access);
        Assert.HasCount(0, authorization.Items);
        Assert.HasCount(0, Loadout(second).DeathInspections);
        OperationReceipt foreign = await second.SendAsync(new DeathInspectCommand
        {
            UnitId = Loadout(second).UnitId, AuthorizationId = authorization.AuthorizationId
        }, token);
        Assert.AreEqual(OperationQueueStatus.Unauthorized, foreign.Status);
        Assert.IsNull(foreign.NextOperationSequence);
        await second.ClearUnqueuedPendingAsync(token);
        Guid corpseItemId = Loadout(second).Inventory!.Items.Single().InstanceId;
        await host.Session.RequestCompletionAsync(token);
        await WaitUntil(() => first.World.State.World.IsCompleted && first.World.State.World.TimeScale == 0, token);
        TimeSpan completedAt = first.World.State.World.Time;
        OperationReceipt forbidden = await first.SendAsync(new TalentAllocateCommand
        {
            UnitId = initial.UnitId, TreeId = "training", NodeId = "practice",
            ExpectedRevision = initial.Talents.Single().Revision,
            ExpectedAuthorityVersion = initial.Talents.Single().AuthorityVersion
        }, token);
        Assert.AreEqual(OperationQueueStatus.Completed, forbidden.Status);
        await first.ClearUnqueuedPendingAsync(token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, new DeathInspectCommand
        {
            UnitId = initial.UnitId, AuthorizationId = authorization.AuthorizationId
        }, token)).Status);
        await WaitUntil(() => Loadout(first).DeathInspections.Single().Access == "None", token);
        DeathInspectionSnapshot inspected = Loadout(first).DeathInspections.Single();
        Assert.AreEqual(corpseItemId, inspected.Items.Single().InstanceId);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, new DeathClaimCommand
        {
            UnitId = initial.UnitId, AuthorizationId = authorization.AuthorizationId,
            ExpectedInspectionRevision = inspected.Revision, Items = [new(corpseItemId)]
        }, token)).Status);
        await WaitUntil(() => Loadout(first).Inventory!.Items.Count == 2 && Loadout(second).Inventory!.Items.Count == 0, token);
        Assert.AreEqual("NotInspected", Loadout(first).DeathInspections.Single().Access);
        await first.RejoinAsync(cancellationToken: token);
        Assert.AreEqual(completedAt, first.World.State.World.Time);
        Assert.IsTrue(first.World.State.World.IsCompleted);
        Assert.AreEqual(0, first.World.State.World.TimeScale);
        Assert.IsTrue(Loadout(first).Inventory!.Items.Any(item => item.InstanceId == corpseItemId));
        Assert.HasCount(0, first.World.State.World.Projectiles);
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine("An existing real death capability exposed no items before an authorized inspection. The completed serialized owner rejected talent mutation but processed inspection and original-item claim at frozen simulation time; rejoin preserved completion and transferred inventory without restarting the old combat queue.");
    }

    [TestMethod]
    public async Task RealNetworkAcceptsNewExchangeMembershipAndNpcDeathInventoryWithoutGrantingNpcAdmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        await using LoopbackSession host = await LoadoutArena.StartAsync(token, dynamicFeatures: true);
        await using GameClient first = LoadoutClient(), second = LoadoutClient();
        await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        Assert.HasCount(0, Loadout(first).Exchanges);
        ItemSnapshot original = Loadout(first).Inventory!.Items.Single();
        Guid previous = Guid.Empty;
        for (int round = 0; round < 2; round++)
        {
            await StopAndWait();
            await WaitUntil(() => Loadout(first).Exchanges.Count == 1 &&
                Loadout(first).Exchanges.Single().InstanceId != previous &&
                Loadout(second).Exchanges.Single().InstanceId == Loadout(first).Exchanges.Single().InstanceId, token);
            ExchangeSnapshot exchange = Loadout(first).Exchanges.Single();
            ItemSnapshot a = Loadout(first).Inventory!.Items.Single(), b = Loadout(second).Inventory!.Items.Single();
            Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, new ExchangeCommand
            {
                UnitId = Loadout(first).UnitId, ExchangeId = exchange.InstanceId, Action = ExchangeAction.Offer,
                ExpectedTermsRevision = 0, Offers = [new(a.InstanceId, "main")]
            }, token)).Status);
            Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(second, new ExchangeCommand
            {
                UnitId = Loadout(second).UnitId, ExchangeId = exchange.InstanceId, Action = ExchangeAction.Offer,
                ExpectedTermsRevision = 1, Offers = [new(b.InstanceId, "main")]
            }, token)).Status);
            foreach (GameClient client in new[] { first, second })
                Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(client, new ExchangeCommand
                {
                    UnitId = Loadout(client).UnitId, ExchangeId = exchange.InstanceId, Action = ExchangeAction.Confirm,
                    ExpectedTermsRevision = 2
                }, token)).Status);
            await WaitUntil(() => Loadout(first).Exchanges.Single().IsClosed &&
                Loadout(second).Exchanges.Single().IsClosed, token);
            Assert.AreEqual(b.InstanceId, Loadout(first).Inventory!.Items.Single().InstanceId);
            CollectionAssert.AreEqual(b.AbilityInstanceIds.ToArray(),
                Loadout(first).Inventory!.Items.Single().AbilityInstanceIds.ToArray());
            Assert.AreEqual(a.InstanceId, Loadout(second).Inventory!.Items.Single().InstanceId);
            previous = exchange.InstanceId;
            await first.RejoinAsync(cancellationToken: token);
            Assert.AreEqual(previous, Loadout(first).Exchanges.Single().InstanceId);
            Assert.IsTrue(Loadout(first).Exchanges.Single().IsClosed);
        }
        Assert.AreEqual(original.InstanceId, Loadout(first).Inventory!.Items.Single().InstanceId);
        CollectionAssert.AreEqual(original.AbilityInstanceIds.ToArray(),
            Loadout(first).Inventory!.Items.Single().AbilityInstanceIds.ToArray());

        await StopAndWait();
        await WaitUntil(() => first.World.State.World.Units.Count == 3, token);
        UnitSnapshot keeper = first.World.State.World.Units.Single(unit =>
            unit.Id != Self(first).Id && unit.Id != Self(second).Id);
        Assert.AreEqual(1, keeper.Life);
        UnitSnapshot caster = Self(first);
        AbilitySnapshot bolt = caster.Abilities.Single(ability => ability.DefinitionId == "bolt");
        int expectedDamage = bolt.Level * 10 + caster.Level;
        Assert.IsGreaterThanOrEqualTo(keeper.Life, expectedDamage);
        OperationReceipt cast = await first.SendAsync(new CastAbilityCommand
        {
            UnitId = caster.Id, MapInstanceId = caster.MapInstanceId!.Value, ControlVersion = caster.ControlVersion,
            AbilityInstanceId = bolt.RuntimeInstanceId, TargetUnitId = keeper.Id
        }, token);
        Assert.AreEqual(OperationQueueStatus.Enqueued, cast.Status, cast.Reason);
        Assert.AreEqual(OperationExecutionStatus.Succeeded,
            (await first.WaitForOutcomeAsync(cast.Sequence, token)).Status);
        await WaitUntil(() => Loadout(first).DeathInspections.Count == 1, token);
        DeathInspectionSnapshot authorization = Loadout(first).DeathInspections.Single();
        Assert.AreEqual(keeper.Id, authorization.OwnerUnitId);
        Assert.AreEqual(0, first.World.State.World.Units.Single(unit => unit.Id == keeper.Id).Life);
        Assert.AreNotEqual(Loadout(first).UnitId, authorization.OwnerUnitId);
        Assert.AreNotEqual(Loadout(second).UnitId, authorization.OwnerUnitId);
        Assert.HasCount(2, await host.Session.GetAdmissionCredentialsAsync(token));
        SessionRequestException binding = await Assert.ThrowsExactlyAsync<SessionRequestException>(() =>
            host.Session.SetControlledUnitAsync(first.Credentials!.ParticipantId, authorization.OwnerUnitId, token));
        Assert.AreEqual(SessionRequestFailure.InvalidRequest, binding.Failure);
        Assert.AreEqual("NotInspected", authorization.Access);
        Assert.HasCount(0, authorization.Items);
        Assert.HasCount(0, Loadout(second).DeathInspections);
        OperationReceipt foreign = await second.SendAsync(new DeathInspectCommand
        {
            UnitId = Loadout(second).UnitId, AuthorizationId = authorization.AuthorizationId
        }, token);
        Assert.AreEqual(OperationQueueStatus.Unauthorized, foreign.Status);
        Assert.IsNull(foreign.NextOperationSequence);
        await second.ClearUnqueuedPendingAsync(token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, new DeathInspectCommand
        {
            UnitId = Loadout(first).UnitId, AuthorizationId = authorization.AuthorizationId
        }, token)).Status);
        await WaitUntil(() => Loadout(first).DeathInspections.Single().Access == "None", token);
        DeathInspectionSnapshot inspected = Loadout(first).DeathInspections.Single();
        ItemSnapshot npcItem = inspected.Items.Single();
        await first.RejoinAsync(cancellationToken: token);
        Assert.AreEqual(npcItem.InstanceId, Loadout(first).DeathInspections.Single().Items.Single().InstanceId);
        Assert.HasCount(0, Loadout(second).DeathInspections);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await Execute(first, new DeathClaimCommand
        {
            UnitId = Loadout(first).UnitId, AuthorizationId = authorization.AuthorizationId,
            ExpectedInspectionRevision = inspected.Revision, Items = [new(npcItem.InstanceId)]
        }, token)).Status);
        await WaitUntil(() => Loadout(first).Inventory!.Items.Count == 2 &&
            Loadout(first).DeathInspections.Count == 0, token);
        ItemSnapshot claimed = Loadout(first).Inventory!.Items.Single(item => item.InstanceId == npcItem.InstanceId);
        Assert.HasCount(1, claimed.AbilityInstanceIds);
        Assert.AreNotEqual(original.AbilityInstanceIds.Single(), claimed.AbilityInstanceIds.Single());
        await first.RejoinAsync(cancellationToken: token);
        Assert.HasCount(0, Loadout(first).DeathInspections);
        Assert.IsTrue(Loadout(first).Inventory!.Items.Any(item => item.InstanceId == npcItem.InstanceId));
        OperationReceipt removed = await first.SendAsync(new DeathInspectCommand
        {
            UnitId = Loadout(first).UnitId, AuthorizationId = authorization.AuthorizationId
        }, token);
        Assert.AreEqual(OperationQueueStatus.Unauthorized, removed.Status);
        Assert.IsNull(removed.NextOperationSequence);
        await first.ClearUnqueuedPendingAsync(token);
        Assert.IsNull(first.ObservationFailure);
        Assert.IsNull(second.ObservationFailure);
        TestContext.WriteLine("The serialized mode published two new exchange memberships after admission; both real bilateral exchanges retained original item/ability identities and the second returned the original item. Each rejoin saw only the current exchange. A later NPC inventory/directory accepted real lethal damage and beneficiary-only inspection/claim without adding a login or allowing control of the NPC. Actual domain cleanup and topology removal cleared the inspection live and on rejoin; stale authorization and foreign inspection were denied without sequence hints.");

        async Task StopAndWait()
        {
            OperationReceipt receipt = await first.SendAsync(Stop(first), token);
            Assert.AreEqual(OperationQueueStatus.Enqueued, receipt.Status, receipt.Reason);
            Assert.AreEqual(OperationExecutionStatus.Succeeded,
                (await first.WaitForOutcomeAsync(receipt.Sequence, token)).Status);
        }
    }

    [TestMethod]
    public async Task LoadoutTopologyRechecksQueuedInputAndRetainsResultIdentityAfterFeatureRemoval()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        var (simulation, units, _) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token);
        using Context context = simulation.Context;
        Unit unit = units[0];
        var definition = new TalentTreeDefinition("retained", "Retained", 2,
            [new("basic", "Basic", [new("practice", "Practice", 2, [])])]);
        var talents = new UnitTalents(unit, [definition]);
        unit.EventReceiver.AddHandler(new TalentDirectoryHandler(talents));
        context.Dispatch(context.Factory.NewEvent<TalentAuthorizationRequestEvent>() with
        {
            Owner = unit, Definition = definition, Grant = new AbilitySourceInstance("Role", [], unit),
            Authorize = true, Receiver = unit.EventReceiver
        }).ToList();
        await Drain();
        var authored = new List<LoadoutParticipant> { new(unit, Talents: talents) };
        var original = new LoadoutSessionFeatures(context, authored);
        authored.Clear();
        LoadoutSessionFeatures current = original;
        SessionOperationCompletedEvent? completed = null;
        Guid execution = Guid.NewGuid();
        context.EventProcessed += ev =>
        {
            if (ev is TalentChangeResultEvent { Success: true } change && change.OperationId == execution)
                current = new(context, []);
            if (ev is SessionOperationCompletedEvent result && result.ExecutionId == execution) completed = result;
        };
        var adapter = new LoadoutSessionSimulation(simulation, () => current);
        var tree = talents.Trees[definition.Id];
        var command = new TalentAllocateCommand
        {
            UnitId = unit.Id, TreeId = definition.Id, NodeId = "practice",
            ExpectedRevision = tree.Revision, ExpectedAuthorityVersion = tree.AuthorityVersion
        };
        Assert.IsTrue(adapter.AssessInput(unit.Id, command).Allowed);
        var input = new SessionInput(Guid.NewGuid(), Guid.NewGuid(), unit.Id, 1, 1, Guid.NewGuid(), command);
        current = new(context, []);
        var rejected = (SessionOperationCompletedEvent)adapter.CreateInputEvents(input).Single();
        Assert.AreEqual(OperationExecutionStatus.Rejected, rejected.Status);
        context.Dispatch(rejected).ToList();
        await Drain();
        Assert.HasCount(0, tree.Ranks);

        current = original;
        foreach (IEvent ev in adapter.CreateInputEvents(input with { ExecutionId = execution }))
            context.Dispatch(ev).ToList();
        await Drain();
        Assert.IsNotNull(completed);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, completed.Status);
        Assert.AreEqual(1, tree.Ranks["practice"]);
        Assert.AreEqual(ModuleProjectionKind.Removed, adapter.Observation.Capture(unit.Id, 1, true, null).Kind);
        using var foreign = new Context();
        current = new(foreign, []);
        Assert.ThrowsExactly<InvalidOperationException>(() => adapter.AssessInput(unit.Id, command));
        Assert.ThrowsExactly<InvalidOperationException>(() => adapter.Observation.Capture(unit.Id, 2, true, null));
        TestContext.WriteLine("Membership was copied, not borrowed from its authoring list. Removing a feature after assessment produced a typed rejection without indexing a missing unit. Removing it during a successful domain result did not lose the original submitting receiver or business outcome. Projection removed the private module; a foreign-Context provider failed explicitly.");

        async Task Drain()
        {
            while (context.HasEventsDueNow)
                Assert.IsTrue((await context.NextAsync(token)).KeepRunning);
        }
    }
}
