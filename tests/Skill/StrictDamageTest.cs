using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class StrictDamageTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DamageUsesExplicitAbilityAndCharacterLevelsAtEachHit()
    {
        using var battle = new Battle(
            [new("impact", "ability.level * 10 + source.level")], sourceLevel: 5);
        battle.Hit();
        Assert.AreEqual(185, battle.Target.Property.LifePoint);
        battle.Source.SetLevel(6);
        battle.Hit();
        Assert.AreEqual(169, battle.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 15, 16 }, battle.LifeReductions());
        TestContext.WriteLine("Ability level defaulted to 1; source levels 5 and 6 produced damage 15 then 16.");
    }

    [TestMethod]
    public void DirectDamageReadsCurrentPropertiesWhenItActuallySettles()
    {
        using var battle = new Battle([new("impact", "ability.level * 10 + source.level + target.level")]);
        var observer = new ChangeLevelsBeforeDamage(battle.Source, battle.Target);
        battle.Target.EventReceiver.AddHandler(observer);
        battle.Hit();

        CollectionAssert.AreEqual(new[] { 17 }, battle.LifeReductions());
        Assert.AreEqual(183, battle.Target.Property.LifePoint);
        Assert.IsTrue(observer.SawPendingDamage);
        TestContext.WriteLine("The level-1 effect used source level 4 and target level 3 when damage settled.");
    }

    [TestMethod]
    public void LegacyDirectHitCannotActivateAnotherInstanceWithTheSameDefinitionId()
    {
        using var battle = new Battle([new("impact", "10")]);
        var foreign = new ProjectileAbility { Id = battle.Ability.Id };
        foreach (AbilityRuntimeSnapshot? snapshot in new AbilityRuntimeSnapshot?[]
        {
            AbilityRuntimeSnapshot.Capture(foreign), null
        })
        {
            battle.Context.Dispatch(ProjectileHitPublicationEvent.FromHit(
                battle.Context.Factory.NewEvent<ProjectileHitEvent>() with
                {
                    CommandEventId = Guid.NewGuid(), AbilityId = battle.Ability.Id,
                    AbilitySnapshot = snapshot, Source = battle.Source, Target = battle.Target,
                    Receiver = battle.Source.EventReceiver
                })).ToList();
            battle.AdvanceTo(TimeSpan.Zero);
        }

        Assert.AreEqual(200, battle.Target.Property.LifePoint);
        Assert.IsEmpty(battle.LifeReductions());
        battle.Context.Dispatch(ProjectileHitPublicationEvent.FromHit(
            battle.Context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = battle.Ability.Id,
                AbilitySnapshot = AbilityRuntimeSnapshot.Capture(battle.Ability),
                Source = battle.Source, Target = battle.Target, Receiver = battle.Source.EventReceiver
            })).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("A foreign or missing instance identity did not activate legacy damage; its own instance still settled once.");
    }

    [TestMethod]
    public void LegacyDirectCommandStillCreatesAndSettlesItsOwnProjectile()
    {
        using var battle = new Battle([new("impact", "10")]);
        var map = new MapRuntime();
        foreach ((Unit unit, Vector2D position) in new[]
        {
            (battle.Source, new Vector2D(0, 0)),
            (battle.Target, new Vector2D(1, 0))
        })
        {
            var body = new MapObject(unit.Id.ToString("N"), position, unit.EventReceiver);
            map.AddObject(body);
            unit.MapObjects.Initialize(body);
        }
        battle.Source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"],
                [new MapVisibilityObserver("0", battle.Source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        battle.Source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        battle.Ability.ProjectileSpeed = 0;
        battle.AddCommandTrigger();

        CommandEvent command = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(CommandExecutionStatus.Success, battle.CommandResult(command).Status);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        ProjectileHitEvent hit = battle.Source.EventReceiver.History.OfType<ProjectileHitEvent>().Single();
        Assert.AreEqual(battle.Ability.RuntimeInstanceId, hit.AbilitySnapshot!.RuntimeInstanceId);
        TestContext.WriteLine("The real legacy command emitted its own instant projectile and settled its 10 damage once.");
    }

    [TestMethod]
    public void ActorlessPeriodicDamageReadsTargetPropertiesAtSettlement()
    {
        using var battle = new Battle([new("impact", "ability.level * 10 + target.level")]);
        battle.Target.EventReceiver.AddHandler(new ChangeLevelsBeforeDamage(battle.Source, battle.Target));
        battle.ScheduleDamageWithoutActor(level: 1, count: 1, interval: TimeSpan.Zero);
        battle.AdvanceTo(TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { 13 }, battle.LifeReductions());
        Assert.IsNull(battle.LifeReductionEvents().Single().Source);
        TestContext.WriteLine("The actorless level-1 periodic effect read target level 3 at settlement and preserved its null source.");
    }

    [TestMethod]
    public void OneDamageRequestReachesEligibleListenersAcrossReceiversOnce()
    {
        using var battle = new Battle([new("impact", "10")]);
        Unit observer = battle.AddUnit("observer", team: 1);
        var source = new LifeReductionRecorder { Id = "source-damage" };
        var target = new LifeReductionRecorder { Id = "target-damage" };
        var other = new LifeReductionRecorder { Id = "other-damage" };
        battle.InstallOn(battle.Source, source, DamageTrigger(source.Id, TriggerType.DamageSkillHit, TriggerSourceScope.Self));
        battle.InstallOn(battle.Target, target, DamageTrigger(target.Id, TriggerType.BeingDamaged, TriggerSourceScope.Any));
        battle.InstallOn(observer, other, DamageTrigger(other.Id, TriggerType.DamageSkillHit, TriggerSourceScope.Any));

        battle.Hit();

        DamagePublicationEvent publication = battle.Context.History.OfType<DamagePublicationEvent>().Single();
        DamageEvent[] deliveries = battle.Context.History.OfType<DamageEvent>().ToArray();
        Assert.HasCount(2, deliveries);
        Assert.HasCount(1, deliveries.Where(ev => ev.Receiver == battle.Source.EventReceiver));
        Assert.HasCount(1, deliveries.Where(ev => ev.Receiver == battle.Target.EventReceiver));
        foreach ((LifeReductionRecorder recorder, Unit holder) in new[]
        {
            (source, battle.Source), (target, battle.Target), (other, observer)
        })
        {
            AbilityTriggerInvokeEvent invocation = recorder.Invocations.Single();
            Assert.AreSame(publication.Damage, invocation.UpstreamEvent);
            Assert.AreSame(holder, invocation.Source);
            Assert.AreSame(battle.Target, invocation.Target);
            Assert.AreSame(battle.Source, ((DamageEvent)invocation.UpstreamEvent).Source);
            Assert.AreSame(battle.Target, ((DamageEvent)invocation.UpstreamEvent).Target);
        }
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("One damage request produced two receiver deliveries, one settlement and one response per eligible owner.");
    }

    [TestMethod]
    public void CancelingSourceDamageDeliveryStopsTargetSettlementAndThirdPartyObservation()
    {
        using var battle = new Battle([new("impact", "10")]);
        Unit observer = battle.AddUnit("observer", team: 1);
        var response = new LifeReductionRecorder { Id = "canceled-damage" };
        battle.InstallOn(observer, response, DamageTrigger(response.Id, TriggerType.BeingDamaged, TriggerSourceScope.Any));
        battle.Source.EventReceiver.AddHandler(new CancelDamageRequest { Priority = -10 });

        battle.Hit();

        Assert.IsEmpty(response.Invocations);
        Assert.IsEmpty(battle.LifeReductions());
        Assert.AreEqual(200, battle.Target.Property.LifePoint);
        TestContext.WriteLine("Canceling the source delivery stopped the target settlement and the later third-party damage observation.");
    }

    [TestMethod]
    public void ActorlessDamageMatchesAnyButNotSelfWithoutInventingAnActor()
    {
        using var battle = new Battle([new("impact", "10")]);
        Unit observer = battle.AddUnit("observer", team: 1);
        var target = new LifeReductionRecorder { Id = "actorless-target" };
        var other = new LifeReductionRecorder { Id = "actorless-other" };
        battle.InstallOn(battle.Target, target, DamageTrigger(target.Id, TriggerType.BeingDamaged, TriggerSourceScope.Any));
        battle.InstallOn(observer, other, DamageTrigger(other.Id, TriggerType.DamageSkillHit, TriggerSourceScope.Self));
        battle.ScheduleDamageWithoutActor(level: 1, count: 1, interval: TimeSpan.Zero);
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.HasCount(1, target.Invocations);
        Assert.IsEmpty(other.Invocations);
        Assert.IsNull(((DamageEvent)target.Invocations[0].UpstreamEvent).Source);
        Assert.AreSame(battle.Target, target.Invocations[0].Source);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("Actorless damage matched Any but not Self; the response belonged to its listener.");
    }

    [TestMethod]
    public void DistinctDamageRequestsWithTheSameCommandAreNotCollapsed()
    {
        using var battle = new Battle([new("impact", "10")]);
        Unit observer = battle.AddUnit("observer", team: 1);
        var response = new LifeReductionRecorder { Id = "two-damage" };
        battle.InstallOn(observer, response, DamageTrigger(response.Id, TriggerType.DamageSkillHit, TriggerSourceScope.Any));
        DamageProfile profile = battle.Profile(1);
        Guid command = Guid.NewGuid();
        for (int i = 0; i < 2; i++)
        {
            var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
            { CommandEventId = command, Source = battle.Source, Target = battle.Target, Receiver = battle.Source.EventReceiver };
            damage.AppendDamage(profile.Calculate(battle.Source, battle.Target, TimeSpan.Zero));
            battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        }
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.HasCount(2, response.Invocations);
        Assert.AreNotSame(response.Invocations[0].UpstreamEvent, response.Invocations[1].UpstreamEvent);
        CollectionAssert.AreEqual(new[] { 10, 10 }, battle.LifeReductions());
        TestContext.WriteLine("Two real damage requests with the same command each published and settled once.");
    }

    [TestMethod]
    public void DeferredDamagePublishesOnceAtReplayAndUsesThenCurrentListeners()
    {
        using var battle = new Battle([new("impact", "10")]);
        Unit observer = battle.AddUnit("observer", team: 1);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        var requests = new List<DamageRequestEvent>();
        var publications = new List<DamagePublicationEvent>();
        battle.Context.EventProcessed += ev =>
        {
            if (ev is DamageRequestEvent request) requests.Add(request);
            if (ev is DamagePublicationEvent publication) publications.Add(publication);
        };
        battle.Hit();
        Assert.IsEmpty(battle.Context.History.OfType<DamageEvent>());
        Assert.IsEmpty(battle.LifeReductions());
        Assert.IsEmpty(publications);
        DamageRequestEvent deferredRequest = requests.Single();
        Assert.ThrowsExactly<InvalidOperationException>(() => deferredRequest.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => (deferredRequest with { }).Dispatch().ToArray());
        var forgedRequest = battle.Context.Factory.NewEvent<DamageRequestEvent>() with
        { Damage = deferredRequest.Damage, Receiver = deferredRequest.Receiver };
        var forgedPublication = battle.Context.Factory.NewEvent<DamagePublicationEvent>() with
        { Damage = deferredRequest.Damage, Receiver = deferredRequest.Receiver };
        Assert.ThrowsExactly<InvalidOperationException>(() => forgedRequest.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => forgedPublication.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            (deferredRequest with { Damage = deferredRequest.Damage with { } }).Dispatch().ToArray());
        var response = new LifeReductionRecorder { Id = "later-observer" };
        battle.InstallOn(observer, response, DamageTrigger(response.Id, TriggerType.BeingDamaged, TriggerSourceScope.Any));

        settlement.BeginReplay();
        var damage = (DamageEvent)settlement.TakeNext()!;
        Assert.IsNull(settlement.TakeNext());
        battle.Context.Dispatch(battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
        { Settlement = settlement, Damage = damage, Receiver = damage.Receiver }).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        settlement.Complete();

        Assert.HasCount(1, publications);
        Assert.AreSame(damage, publications[0].Damage);
        Assert.HasCount(2, battle.Context.History.OfType<DamageEvent>());
        Assert.HasCount(1, response.Invocations);
        Assert.AreSame(damage, response.Invocations[0].UpstreamEvent);
        Assert.AreSame(observer, response.Invocations[0].Source);
        Assert.AreSame(battle.Target, response.Invocations[0].Target);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("The deferred request remained one event and reached a listener installed before actual replay exactly once.");
    }

    [TestMethod]
    public void DamageRequestsRejectDirectAndRepeatedPublication()
    {
        using var battle = new Battle([new("impact", "10")]);
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        { CommandEventId = Guid.NewGuid(), Source = battle.Source, Target = battle.Target,
            Receiver = battle.Source.EventReceiver };
        damage.AppendDamage(profile.Calculate(battle.Source, battle.Target, TimeSpan.Zero));
        Assert.ThrowsExactly<InvalidOperationException>(() => damage.Dispatch().ToArray());
        DamageRequestEvent request = DamageRequestEvent.FromDamage(damage);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            damage.AppendDamage(profile.Calculate(battle.Source, battle.Target, TimeSpan.Zero)));
        Assert.ThrowsExactly<InvalidOperationException>(() => DamageRequestEvent.FromDamage(damage with { }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            (request with { Damage = damage with { } }).Dispatch().ToArray());
        battle.Context.Dispatch(request).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.ThrowsExactly<InvalidOperationException>(() => request.Dispatch().ToArray());
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("Direct, cloned and repeated damage publication were rejected without a second settlement.");
    }

    [TestMethod]
    public void SettlementStartedAfterDamageAdmissionDoesNotRetroactivelyDeferIt()
    {
        using var battle = new Battle([new("impact", "10")]);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = battle.Source, Target = battle.Target,
            Receiver = battle.Source.EventReceiver
        };
        damage.AppendDamage(battle.Profile(1).Calculate(battle.Source, battle.Target, TimeSpan.Zero));
        var request = DamageRequestEvent.FromDamage(damage);
        battle.Context.Dispatch(request).ToList();
        IEvent? processed;
        do
        {
            Assert.IsTrue(battle.Context.Next(out processed));
            Assert.IsNotNull(processed);
        } while (!ReferenceEquals(processed, request));
        using var settlement = new DeferredCombatImpacts(battle.Target);
        battle.AdvanceTo(TimeSpan.Zero);
        settlement.BeginReplay();
        Assert.IsNull(settlement.TakeNext());
        settlement.Complete();
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("A settlement started after admission did not defer the already admitted hit.");
    }

    [TestMethod]
    public void SelfDamagePublishesOneLifeReductionToTheSharedReceiver()
    {
        using var battle = new Battle([new("impact", "10")]);
        var recorder = new LifeReductionRecorder { Id = "self-response" };
        battle.InstallOnTarget(recorder, LifeTrigger(recorder.Id, TriggerSourceScope.Self));
        Damage damage = battle.Profile(1).Calculate(battle.Target, battle.Target, TimeSpan.Zero);
        DamageEvent ev = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = battle.Target, Target = battle.Target,
            Receiver = battle.Target.EventReceiver
        };
        ev.AppendDamage(damage);
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(ev)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(190, battle.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        Assert.HasCount(1, recorder.Invocations);
        Assert.AreSame(battle.Target, recorder.Invocations[0].Source);
        TestContext.WriteLine("Self-damage reached the shared receiver and its Self listener once.");
    }

    [TestMethod]
    public void LifeReductionListenerObservesDifferentAndNullActorsOnceEach()
    {
        using var battle = new Battle([new("impact", "10")]);
        var recorder = new LifeReductionRecorder { Id = "life-response" };
        var trigger = new TriggerHandler
        {
            Id = "life-trigger",
            TargetAbilityId = recorder.Id,
            Config = new AbilityTriggerConfig
            {
                Type = TriggerType.TargetLifeReduce,
                EventSourceScope = TriggerSourceScope.Any
            }
        };
        battle.InstallOnTarget(recorder, trigger);

        battle.Hit();
        battle.ScheduleDamageWithoutActor(level: 1, count: 1, interval: TimeSpan.Zero);
        battle.AdvanceTo(TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { 10, 10 }, battle.LifeReductions());
        Assert.HasCount(2, recorder.Invocations);
        Assert.IsTrue(recorder.Invocations.All(ev => ReferenceEquals(ev.Source, battle.Target)));
        Assert.AreSame(battle.Source, ((DamageLifeReduceEvent)recorder.Invocations[0].UpstreamEvent).Source);
        Assert.IsNull(((DamageLifeReduceEvent)recorder.Invocations[1].UpstreamEvent).Source);
        TestContext.WriteLine("Two real life reductions, including an actorless tick, each reached the target's Any-scope listener once.");
    }

    [TestMethod]
    public void LifeReductionRoutesEachScopeOnceAndUsesTheRespondersOwnMapObject()
    {
        using var battle = new Battle([new("impact", "10")]);
        battle.Source.TeamId = 1;
        battle.Target.TeamId = 2;
        Unit observer = battle.AddUnit("observer", team: 1);
        var map = new MapRuntime();
        foreach (Unit unit in new[] { battle.Source, battle.Target, observer })
        {
            var body = new MapObject(unit.Id.ToString("N"), new Vector2D(unit.TeamId, 0), unit.EventReceiver);
            map.AddObject(body);
            unit.MapObjects.Initialize(body);
        }
        var sourceResponse = new LifeReductionRecorder { Id = "source-response" };
        var targetResponse = new LifeReductionRecorder { Id = "target-response" };
        var otherResponse = new LifeReductionRecorder { Id = "other-response" };
        battle.InstallOn(battle.Source, sourceResponse, LifeTrigger(sourceResponse.Id, TriggerSourceScope.Self));
        battle.InstallOn(battle.Target, targetResponse, LifeTrigger(targetResponse.Id, TriggerSourceScope.Enemy));
        battle.InstallOn(observer, otherResponse, LifeTrigger(otherResponse.Id, TriggerSourceScope.Any));

        battle.Hit();
        battle.ScheduleDamageWithoutActor(level: 1, count: 1, interval: TimeSpan.Zero);
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.HasCount(1, sourceResponse.Invocations);
        Assert.HasCount(1, targetResponse.Invocations);
        Assert.HasCount(2, otherResponse.Invocations);
        Assert.IsTrue(otherResponse.Invocations.All(ev =>
            ReferenceEquals(ev.Source, observer) && ReferenceEquals(ev.SourceObject, observer.MapObject) &&
            ReferenceEquals(ev.Target, battle.Target)));
        Assert.IsNull(((DamageLifeReduceEvent)otherResponse.Invocations[1].UpstreamEvent).Source);
        TestContext.WriteLine("Source, target and third-party listeners observed eligible reductions once, using the responder's map object.");
    }

    [TestMethod]
    public void EarlierLifeReductionHandlerCanCancelLaterHandlersAndObservers()
    {
        using var battle = new Battle([new("impact", "10")]);
        battle.Target.EventReceiver.AddHandler(new CancelLifeReduction());
        var sourceHandler = new RecordLifeReduction();
        battle.Source.EventReceiver.AddHandler(sourceHandler);
        var targetResponse = new LifeReductionRecorder { Id = "target-response" };
        var sourceResponse = new LifeReductionRecorder { Id = "source-response" };
        battle.InstallOn(battle.Target, targetResponse, LifeTrigger(targetResponse.Id, TriggerSourceScope.Any));
        battle.InstallOn(battle.Source, sourceResponse, LifeTrigger(sourceResponse.Id, TriggerSourceScope.Any));

        battle.Hit();

        Assert.AreEqual(190, battle.Target.Property.LifePoint);
        Assert.IsEmpty(targetResponse.Invocations);
        Assert.IsEmpty(sourceResponse.Invocations);
        Assert.AreEqual(0, sourceHandler.Count);
        Assert.IsTrue(battle.Source.EventReceiver.History.OfType<DamageLifeReduceEvent>()
            .Single().CancellationToken.IsCancellationRequested);
        TestContext.WriteLine("Cancellation in the target handler prevented later source handlers and listeners without undoing settled damage.");
    }

    [TestMethod]
    public void LifeReductionCannotBeRepublishedOrRedelivered()
    {
        using var battle = new Battle([new("impact", "10")]);
        battle.Hit();
        DamageLifeReducePublicationEvent publication = battle.Context.History
            .OfType<DamageLifeReducePublicationEvent>().Single();
        DamageLifeReduceEvent delivery = battle.LifeReductionEvents().Single();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DamageLifeReducePublicationEvent.FromReduction(publication.Reduction));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            DamageLifeReducePublicationEvent.FromReduction(publication.Reduction with { LifePointReduce = 2 }));
        Assert.ThrowsExactly<InvalidOperationException>(() => delivery.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => (delivery with { LifePointReduce = 2 }).Dispatch().ToArray());
        TestContext.WriteLine("The original and copied life-reduction events cannot be republished or redelivered.");
    }

    [TestMethod]
    public void ReinstallingLifeReductionTriggerCannotReviveItsOldObservation()
    {
        using var battle = new Battle([new("impact", "10")]);
        Unit observer = battle.AddUnit("observer", team: 1);
        var recorder = new LifeReductionRecorder { Id = "response" };
        TriggerHandler trigger = LifeTrigger(recorder.Id, TriggerSourceScope.Any);
        trigger.Priority = 10;
        battle.InstallOn(observer, recorder, trigger);

        DamageLifeReducePublicationEvent Publication()
        {
            DamageProfile profile = battle.Profile(1);
            return DamageLifeReducePublicationEvent.FromReduction(
                battle.Context.Factory.NewEvent<DamageLifeReduceEvent>() with
                {
                    CommandEventId = Guid.NewGuid(), DamageAbility = profile,
                    Damage = profile.Calculate(battle.Source, battle.Target, TimeSpan.Zero),
                    Source = battle.Source, Target = battle.Target, LifePointReduce = 10,
                    Receiver = battle.Target.EventReceiver, Priority = -10
                });
        }

        battle.Context.Dispatch(Publication()).ToList();
        battle.Context.Dispatch(battle.Context.Factory.NewEvent<AbilityUninstallEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = observer, Target = observer,
            AbilityIds = [trigger.Id], Receiver = observer.EventReceiver
        }).ToList();
        battle.Context.Dispatch(battle.Context.Factory.NewEvent<AbilityInstallEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = observer, Target = observer,
            Handlers = [trigger], Receiver = observer.EventReceiver
        }).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.IsEmpty(recorder.Invocations);

        battle.Context.Dispatch(Publication()).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.HasCount(1, recorder.Invocations);
        TestContext.WriteLine("An uninstalled and reinstalled trigger ignored its earlier planned observation but observed a later reduction.");
    }

    [TestMethod]
    public void LethalLifeReductionDeliveriesPrecedeTheDeathAttempt()
    {
        using var battle = new Battle([new("impact", "210")]);
        var recorder = new LifeReductionRecorder { Id = "source-response" };
        battle.InstallOn(battle.Source, recorder, LifeTrigger(recorder.Id, TriggerSourceScope.Self));

        battle.Hit();

        foreach (Unit unit in new[] { battle.Target, battle.Source })
        {
            List<IEvent> history = unit.EventReceiver.History;
            int reduction = history.FindIndex(ev => ev is DamageLifeReduceEvent);
            int death = history.FindIndex(ev => ev is DeathEvent);
            Assert.IsGreaterThanOrEqualTo(0, reduction);
            Assert.IsGreaterThan(reduction, death);
        }
        Assert.HasCount(1, recorder.Invocations);
        TestContext.WriteLine("Both life-reduction deliveries retained their ordering ahead of lethal death attempts.");
    }

    [TestMethod]
    public void ConfirmedDeathUsesTheKillerAsEventSourceAndTheVictimAsTarget()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        Unit observer = battle.AddUnit("observer", team: 1);
        var sourceResponse = new LifeReductionRecorder { Id = "source-death" };
        var victimResponse = new LifeReductionRecorder { Id = "victim-death" };
        var otherResponse = new LifeReductionRecorder { Id = "other-death" };
        battle.InstallOn(battle.Source, sourceResponse, DeathTrigger(sourceResponse.Id, TriggerSourceScope.Self));
        battle.InstallOn(battle.Target, victimResponse, DeathTrigger(victimResponse.Id, TriggerSourceScope.Any));
        battle.InstallOn(observer, otherResponse, DeathTrigger(otherResponse.Id, TriggerSourceScope.Any));

        battle.Hit();

        Assert.IsFalse(battle.Target.IsAlive);
        Assert.HasCount(1, sourceResponse.Invocations);
        Assert.HasCount(1, victimResponse.Invocations);
        Assert.HasCount(1, otherResponse.Invocations);
        foreach (LifeReductionRecorder recorder in new[] { sourceResponse, victimResponse, otherResponse })
        {
            AbilityTriggerInvokeEvent invocation = recorder.Invocations.Single();
            var death = (DeathResolvedEvent)invocation.UpstreamEvent;
            Assert.AreSame(battle.Source, death.Killer);
            Assert.AreSame(battle.Target, death.Victim);
            Assert.AreSame(battle.Target, invocation.Target);
        }
        Assert.AreSame(battle.Source, sourceResponse.Invocations[0].Source);
        Assert.AreSame(battle.Target, victimResponse.Invocations[0].Source);
        Assert.AreSame(observer, otherResponse.Invocations[0].Source);
        TestContext.WriteLine("One confirmed death was observed by eligible source, victim and third-party listeners with distinct event and response roles.");
    }

    [TestMethod]
    public void PendingDeathAndConfirmedDeathAreDistinctSinglePublications()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        Unit observer = battle.AddUnit("observer", team: 1);
        var pending = new LifeReductionRecorder { Id = "pending-death" };
        var confirmed = new LifeReductionRecorder { Id = "confirmed-death" };
        battle.InstallOn(observer, pending,
            DeathTrigger(pending.Id, TriggerSourceScope.Any, DeathTriggerPhase.Pending));
        battle.InstallOn(observer, confirmed,
            DeathTrigger(confirmed.Id, TriggerSourceScope.Any, DeathTriggerPhase.Confirmed));

        battle.Hit();

        Assert.HasCount(1, pending.Invocations);
        Assert.HasCount(1, confirmed.Invocations);
        var attempt = (DeathEvent)pending.Invocations[0].UpstreamEvent;
        var resolution = (DeathResolvedEvent)confirmed.Invocations[0].UpstreamEvent;
        Assert.AreEqual(attempt.AttemptId, resolution.DeathId);
        Assert.AreSame(battle.Source, attempt.Killer);
        Assert.AreSame(battle.Target, attempt.Victim);
        Assert.AreSame(observer, pending.Invocations[0].Source);
        Assert.AreSame(battle.Target, pending.Invocations[0].Target);
        Assert.AreSame(observer, confirmed.Invocations[0].Source);
        Assert.AreSame(battle.Target, confirmed.Invocations[0].Target);
        TestContext.WriteLine("The pending and confirmed phases each reached the third-party listener once with the same death identity.");
    }

    [TestMethod]
    public void ThirdPartyPendingDeathResponseCanCancelBeforeCommit()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        Unit observer = battle.AddUnit("observer", team: 1);
        var interceptor = new CancelPendingDeathResponse { Id = "intercept" };
        battle.InstallOn(observer, interceptor,
            DeathTrigger(interceptor.Id, TriggerSourceScope.Any, DeathTriggerPhase.Pending));

        battle.Hit();

        Assert.AreEqual(1, interceptor.Count);
        Assert.IsTrue(battle.Target.IsAlive);
        Assert.IsEmpty(battle.Target.EventReceiver.History.OfType<DeathResolvedEvent>());
        TestContext.WriteLine("A third-party pending-death response canceled the shared attempt before commit; no confirmed death was published.");
    }

    [TestMethod]
    public void ActorlessConfirmedDeathMatchesAnyButNotSelf()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        Unit observer = battle.AddUnit("observer", team: 1);
        var any = new LifeReductionRecorder { Id = "any-death" };
        var self = new LifeReductionRecorder { Id = "self-death" };
        battle.InstallOn(battle.Target, any, DeathTrigger(any.Id, TriggerSourceScope.Any));
        battle.InstallOn(observer, self, DeathTrigger(self.Id, TriggerSourceScope.Self));

        battle.ScheduleDamageWithoutActor(level: 1, count: 1, interval: TimeSpan.Zero);
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.HasCount(1, any.Invocations);
        Assert.IsNull(((DeathResolvedEvent)any.Invocations.Single().UpstreamEvent).Killer);
        Assert.IsEmpty(self.Invocations);
        TestContext.WriteLine("An actorless confirmed death matched Any and did not invent a Self killer.");
    }

    [TestMethod]
    public void CanceledDeathAttemptDoesNotPublishAConfirmedDeath()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new CancelDeathAttempt(), new DeathAbility { Id = "death" });
        var recorder = new LifeReductionRecorder { Id = "death-response" };
        battle.InstallOn(battle.Source, recorder, DeathTrigger(recorder.Id, TriggerSourceScope.Any));

        battle.Hit();

        Assert.IsTrue(battle.Target.IsAlive);
        Assert.IsEmpty(battle.Target.EventReceiver.History.OfType<DeathResolvedEvent>());
        Assert.IsEmpty(recorder.Invocations);
        TestContext.WriteLine("A canceled pending death did not publish or route a confirmed death.");
    }

    [TestMethod]
    public void ConfirmedDeathCannotBePublishedAgainOrCloned()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        battle.Hit();
        DeathResolvedEvent death = battle.Target.EventReceiver.History.OfType<DeathResolvedEvent>().Single();

        Assert.ThrowsExactly<InvalidOperationException>(() => death.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => (death with { Killer = null }).Dispatch().ToArray());
        TestContext.WriteLine("Neither a confirmed death nor its modified record copy can be published again.");
    }

    [TestMethod]
    public void ConfirmedDeathRewardUsesTheRealKillerRatherThanTheTriggerTarget()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        var reward = new ExperienceRewardAbility { Id = "reward", Amount = 10 };
        battle.InstallOnTarget(reward, DeathTrigger(reward.Id, TriggerSourceScope.Any));

        battle.Hit();

        Assert.IsFalse(battle.Target.IsAlive);
        Assert.AreEqual(10, battle.Source.Property.Experience);
        Assert.AreEqual(0, battle.Target.Property.Experience);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<UnitExperienceChangedEvent>());
        TestContext.WriteLine("The confirmed-death reward credited the original killer, not the listener or victim.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ConfirmedDeathRewardAppliesConfiguredKillerAndVictimTagFilters(bool tagsMatch)
    {
        using var battle = new Battle([new("impact", "210")],
            sourceTags: new HashSet<string>(["player"]),
            targetTags: new HashSet<string>([tagsMatch ? "dummy" : "other"]));
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        var reward = new ExperienceRewardAbility
        {
            Id = "filtered-reward", Amount = 10,
            RequiredVictimTags = ["dummy"], RequiredKillerTags = ["player"]
        };
        battle.InstallOnTarget(reward, DeathTrigger(reward.Id, TriggerSourceScope.Any));

        battle.Hit();

        Assert.AreEqual(tagsMatch ? 10 : 0, battle.Source.Property.Experience);
        TestContext.WriteLine($"Confirmed death reward tag filters matched={tagsMatch}; granted experience={battle.Source.Property.Experience}.");
    }

    [TestMethod]
    public void ActorlessDeathDoesNotManufactureAnExperienceRecipient()
    {
        using var battle = new Battle([new("impact", "210")]);
        battle.Target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        var reward = new ExperienceRewardAbility { Id = "reward", Amount = 10 };
        battle.InstallOnTarget(reward, DeathTrigger(reward.Id, TriggerSourceScope.Any));
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target,
            Receiver = battle.Target.EventReceiver
        };
        damage.AppendDamage(battle.Profile(1).Calculate(null, battle.Target, TimeSpan.Zero));
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.IsNull(battle.Target.EventReceiver.History.OfType<DeathResolvedEvent>().Single().Killer);
        Assert.AreEqual(0, battle.Source.Property.Experience);
        Assert.AreEqual(0, battle.Target.Property.Experience);
        Assert.IsEmpty(battle.Context.History.OfType<UnitExperienceChangedEvent>());
        TestContext.WriteLine("An actorless confirmed death did not invent an experience recipient.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnsupportedEventRoleAreaSelectorsFailExplicitly(bool circle)
    {
        using var battle = new Battle([new("impact", "10")]);
        var registry = new UnitSpatialRegistry();
        registry.Register(battle.Source);
        registry.Register(battle.Target);
        battle.Source.Registry = registry;
        battle.Target.Registry = registry;
        battle.Ability.Selector = circle
            ? new CircleSelector { Origin = TargetingOrigin.EventSource, Radius = 1 }
            : new LineSelector { Origin = TargetingOrigin.EventTarget, Range = 1, Width = 1 };

        var error = Assert.ThrowsExactly<InvalidOperationException>(battle.Hit);
        StringAssert.Contains(error.Message, "do not support event-role origins");
        Assert.IsEmpty(battle.LifeReductions());
        TestContext.WriteLine("An unsupported event-role area selector failed explicitly without applying damage.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(int.MaxValue)]
    public void DeferredDamageMustBeTakenAndCanBeReplayedOnlyOnce(int priority)
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target,
            Receiver = battle.Target.EventReceiver, Priority = priority
        };
        damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        var completion = new DeferredReplayRecorder(battle.Target);
        battle.Source.EventReceiver.AddHandler(completion);
        settlement.BeginReplay();
        var replay = battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
        { Settlement = settlement, Damage = damage, Receiver = battle.Target.EventReceiver,
            CompletionReceiver = battle.Source.EventReceiver };
        Assert.ThrowsExactly<InvalidOperationException>(() => replay.Dispatch().ToArray());
        Assert.AreSame(damage, settlement.TakeNext());

        var wrongReceiver = replay with { Receiver = battle.Source.EventReceiver };
        var copiedDamage = replay with { Damage = damage with { } };
        using var foreign = new Battle([new("impact", "10")]);
        using var independentCancellation = new CancellationTokenSource();
        var foreignCompletion = replay with { CompletionReceiver = foreign.Source.EventReceiver };
        var wrongCancellation = replay with { CancellationTokenSource = independentCancellation };
        Assert.ThrowsExactly<InvalidOperationException>(() => wrongReceiver.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => copiedDamage.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => foreignCompletion.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => wrongCancellation.Dispatch().ToArray());
        Assert.IsEmpty(battle.LifeReductions());
        Assert.ThrowsExactly<InvalidOperationException>(settlement.Complete);

        battle.Context.Dispatch(replay).ToList();
        IEvent? processed;
        do
        {
            Assert.IsTrue(battle.Context.Next(out processed));
            Assert.IsNotNull(processed);
        } while (!ReferenceEquals(processed, replay));
        Assert.ThrowsExactly<InvalidOperationException>(settlement.Complete);
        Assert.IsEmpty(completion.Events);
        battle.AdvanceTo(TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        Assert.HasCount(1, completion.Events);
        Assert.AreSame(damage, completion.Events[0].Damage);
        Assert.AreEqual(190, completion.LifeAtCompletion);
        Assert.ThrowsExactly<InvalidOperationException>(() => replay.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => (replay with { }).Dispatch().ToArray());
        Assert.IsNull(settlement.TakeNext());
        settlement.Complete();
        Assert.AreEqual(190, battle.Target.Property.LifePoint);
        TestContext.WriteLine("Only the taken damage and its original receiver replayed; completion was notified after settlement and forged, foreign or repeated requests failed.");
    }

    [TestMethod]
    public void RemovedReplayCompletionReceiverFailsInsteadOfLosingNotification()
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        { CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target, Receiver = battle.Target.EventReceiver };
        damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        settlement.BeginReplay();
        Assert.AreSame(damage, settlement.TakeNext());
        var replay = battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
        { Settlement = settlement, Damage = damage, Receiver = battle.Target.EventReceiver,
            CompletionReceiver = battle.Source.EventReceiver };
        battle.Context.Dispatch(replay).ToList();
        IEvent? processed;
        do
        {
            Assert.IsTrue(battle.Context.Next(out processed));
            Assert.IsNotNull(processed);
        } while (!ReferenceEquals(processed, replay));
        battle.Context.RemoveEventReceiver(battle.Source.EventReceiver);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => battle.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "no longer registered");
        Assert.ThrowsExactly<InvalidOperationException>(settlement.Complete);
        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        TestContext.WriteLine("Removing the completion receiver reported failure without silently completing or rolling back settled damage.");
    }

    [TestMethod]
    public void IndependentlyCanceledInFlightReplayDoesNotSilentlyComplete()
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        using EventScope scope = battle.Context.CreateEventScope();
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        { CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target, Receiver = battle.Target.EventReceiver };
        damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        settlement.BeginReplay();
        Assert.AreSame(damage, settlement.TakeNext());
        var replay = battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
        { Settlement = settlement, Damage = damage, Receiver = battle.Target.EventReceiver, Scope = scope };
        battle.Context.Dispatch(replay).ToList();
        IEvent? processed;
        do
        {
            Assert.IsTrue(battle.Context.Next(out processed));
            Assert.IsNotNull(processed);
        } while (!ReferenceEquals(processed, replay));
        scope.Cancel();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => battle.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.Message, "canceled independently");
        Assert.ThrowsExactly<InvalidOperationException>(settlement.Complete);
        Assert.IsEmpty(battle.LifeReductions());
        TestContext.WriteLine("A replay canceled independently after dispatch reported failure and could not silently complete the settlement.");
    }

    [TestMethod]
    public void OriginalCancellationAfterReplayAdmissionDoesNotPublishDamage()
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        using var cancellation = new CancellationTokenSource();
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target,
            Receiver = battle.Target.EventReceiver, CancellationTokenSource = cancellation
        };
        damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
        var publications = new List<DamagePublicationEvent>();
        battle.Context.EventProcessed += ev =>
        {
            if (ev is DamagePublicationEvent publication) publications.Add(publication);
        };
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        var completion = new DeferredReplayRecorder(battle.Target);
        battle.Source.EventReceiver.AddHandler(completion);
        settlement.BeginReplay();
        Assert.AreSame(damage, settlement.TakeNext());
        var replay = battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
        {
            Settlement = settlement, Damage = damage, Receiver = battle.Target.EventReceiver,
            CompletionReceiver = battle.Source.EventReceiver
        };
        battle.Context.Dispatch(replay).ToList();
        IEvent? processed;
        do
        {
            Assert.IsTrue(battle.Context.Next(out processed));
            Assert.IsNotNull(processed);
        } while (!ReferenceEquals(processed, replay));
        cancellation.Cancel();
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.IsEmpty(publications);
        Assert.IsEmpty(battle.LifeReductions());
        Assert.HasCount(1, completion.Events);
        settlement.Complete();
        TestContext.WriteLine("Canceling the original after replay admission published no damage and still completed the replay.");
    }

    [TestMethod]
    public void DeferredDamageRetainsItsInheritedScopeAfterPublication()
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        using EventScope scope = battle.Context.CreateEventScope();
        var emitter = new ScopedDamageEmitter(battle.Source, battle.Target, battle.Profile(1));
        battle.Source.EventReceiver.AddHandler(emitter);
        battle.Context.Dispatch(battle.Context.Factory.NewEvent<ScopedDamageRequestEvent>() with
        { Receiver = battle.Source.EventReceiver, Scope = scope }).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.IsNotNull(emitter.Damage);
        Assert.IsEmpty(battle.LifeReductions());
        settlement.BeginReplay();
        Assert.AreSame(emitter.Damage, settlement.TakeNext());
        scope.Cancel();
        settlement.Complete();
        Assert.AreEqual(200, battle.Target.Property.LifePoint);
        TestContext.WriteLine("A deferred damage publication retained its inherited scope and was safely discarded when that scope was canceled.");
    }

    [TestMethod]
    public void CanceledTakenDamageDoesNotPreventSettlementCompletion()
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        using var cancellation = new CancellationTokenSource();
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target,
            Receiver = battle.Target.EventReceiver, CancellationTokenSource = cancellation
        };
        damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        settlement.BeginReplay();
        Assert.AreSame(damage, settlement.TakeNext());
        cancellation.Cancel();
        settlement.Complete();
        Assert.IsEmpty(battle.LifeReductions());
        Assert.AreEqual(200, battle.Target.Property.LifePoint);
        TestContext.WriteLine("A taken impact canceled before replay was discarded without damage or blocking settlement cleanup.");
    }

    [TestMethod]
    public void IndependentDeferredDamageEventsSharingACommandReplayInOrder()
    {
        using var battle = new Battle([new("impact", "10")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        DamageProfile profile = battle.Profile(1);
        Guid command = Guid.NewGuid();
        var impacts = new List<DamageEvent>();
        for (int i = 0; i < 2; i++)
        {
            var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
            { CommandEventId = command, Source = null, Target = battle.Target, Receiver = battle.Target.EventReceiver };
            damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
            impacts.Add(damage);
            battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        }
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.IsEmpty(battle.LifeReductions());
        settlement.BeginReplay();
        foreach (DamageEvent expected in impacts)
        {
            Assert.AreSame(expected, settlement.TakeNext());
            battle.Context.Dispatch(battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
            { Settlement = settlement, Damage = expected, Receiver = expected.Receiver }).ToList();
            battle.AdvanceTo(TimeSpan.Zero);
        }
        Assert.IsNull(settlement.TakeNext());
        settlement.Complete();
        CollectionAssert.AreEqual(new[] { 10, 10 }, battle.LifeReductions());
        Assert.AreEqual(180, battle.Target.Property.LifePoint);
        TestContext.WriteLine("Two independent deferred impacts with the same command replayed in enqueue order and each settled once.");
    }

    [TestMethod]
    public void DeferredDamageKeepsItsReleasedAbilityLevelAndReadsCurrentUnitLevelsOnReplay()
    {
        using var battle = new Battle([new("impact", "ability.level * 10 + source.level + target.level")]);
        var recorder = new LifeReductionRecorder { Id = "deferred-response" };
        battle.InstallOnTarget(recorder, LifeTrigger(recorder.Id, TriggerSourceScope.Any));
        using var deferred = new DeferredCombatImpacts(battle.Target);
        battle.Hit();
        Assert.IsEmpty(battle.LifeReductions());
        Assert.IsEmpty(recorder.Invocations);
        battle.SetDamageLevel(5);
        battle.Source.SetLevel(4);
        battle.Target.SetLevel(3);

        deferred.BeginReplay();
        while (deferred.TakeNext() is Event pending)
        {
            Assert.IsInstanceOfType<DamageEvent>(pending);
            var damage = (DamageEvent)pending;
            battle.Context.Dispatch(battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
            {
                Settlement = deferred, Damage = damage, Receiver = damage.Receiver,
                Causality = damage.Causality
            }).ToList();
            battle.AdvanceTo(battle.Context.ContextTimeSpan);
        }
        deferred.Complete();

        CollectionAssert.AreEqual(new[] { 17 }, battle.LifeReductions());
        Assert.AreEqual(183, battle.Target.Property.LifePoint);
        Assert.HasCount(1, recorder.Invocations);
        Assert.AreSame(battle.Source, ((DamageLifeReduceEvent)recorder.Invocations[0].UpstreamEvent).Source);
        TestContext.WriteLine("Deferred replay used the released level, read later properties and published one life reduction only on settlement.");
    }

    [TestMethod]
    public void QuantizedComponentsAreSummedBeforeLifeReductionWithoutArmor()
    {
        using var battle = new Battle([new("first", "0.60"), new("second", "0.60")]);
        battle.Hit();
        Assert.AreEqual(199, battle.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 1 }, battle.LifeReductions());

        using var rounded = new Battle([new("impact", "65.999")]);
        rounded.Hit();
        Assert.AreEqual(134, rounded.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 66 }, rounded.LifeReductions());

        using var fractional = new Battle([new("impact", "0.99")]);
        fractional.Hit();
        Assert.AreEqual(200, fractional.Target.Property.LifePoint);
        Assert.IsEmpty(fractional.LifeReductions());

        using var midpoint = new Battle([new("first", "1.015"), new("second", "0.985")]);
        midpoint.Hit();
        Assert.AreEqual(198, midpoint.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 2 }, midpoint.LifeReductions());
        using var belowMidpoint = new Battle([new("first", "1.0149999999999997"), new("second", "0.985")]);
        belowMidpoint.Hit();
        Assert.AreEqual(199, belowMidpoint.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 1 }, belowMidpoint.LifeReductions());
        TestContext.WriteLine("0.60 + 0.60 reduced 1; 65.999 reduced 66; decimal midpoints rounded to even.");
    }

    [TestMethod]
    public void ExplicitlyRegisteredArmorAndLevelsDriveMitigation()
    {
        const string mitigation = "raw * (100 + 10 * max(0, source.level - target.level)) / " +
                                  "(100 + 10 * max(0, source.level - target.level) + armor)";
        using var battle = new Battle([new("impact", "100")], sourceLevel: 6, targetLevel: 1,
            mitigation: mitigation, armor: 100);
        battle.Hit();
        Assert.AreEqual(140, battle.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 60 }, battle.LifeReductions());
        TestContext.WriteLine("With raw 100, armor 100 and level gap 5, configured mitigation reduced 60 life.");
    }

    [TestMethod]
    public void DamageComponentsFollowPriorityThenDeclarationOrder()
    {
        using var ordered = new Battle([
            new("impact", "10", Priority: 10),
            new("impact", "raw * 2", Priority: 0)
        ]);
        ordered.Hit();
        Assert.AreEqual(190, ordered.Target.Property.LifePoint);

        using var tied = new Battle([
            new("impact", "10"),
            new("impact", "raw")
        ]);
        tied.Hit();
        Assert.AreEqual(180, tied.Target.Property.LifePoint);
        TestContext.WriteLine("Components used ascending priority and preserved declaration order for equal priorities.");
    }

    [TestMethod]
    public void NegativeDamageTypesDoNotCancelOtherTypesOrTheirMitigation()
    {
        using var battle = new Battle([
            new("negative", "-50"),
            new("impact", "10")
        ], mitigation: "raw / 2");
        battle.Hit();
        DamageLifeReduceEvent result = battle.LifeReductionEvents().Single();
        Assert.AreEqual(195, battle.Target.Property.LifePoint);
        Assert.AreEqual(5, result.LifePointReduce);
        Assert.AreEqual(5, result.MitigatedDamage);
        TestContext.WriteLine("The negative type contributed zero; ten raw damage produced five damage and five mitigation.");
    }

    [TestMethod]
    public void DamageComponentsRemainConsistentWhenRemovedOrReplaced()
    {
        using var battle = new Battle([new("impact", "10"), new("other", "2")]);
        battle.Hit();
        Damage damage = battle.TargetDamageEvents().Single().TotalDamage;

        Assert.IsTrue(damage.Remove("IMPACT"));
        Assert.IsFalse(damage.Remove("impact"));
        Assert.AreEqual(200, damage.SumTrackedValues());
        CollectionAssert.AreEqual(new[] { "other" }, damage.StoredKeys.ToArray());

        damage["IMPACT"] = 300;
        damage["impact"] = 400;
        Assert.AreEqual(600, damage.SumTrackedValues());
        CollectionAssert.AreEqual(new[] { "other", "IMPACT" }, damage.StoredKeys.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => damage.Add("impact", 1));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string>)damage.StoredKeys).Clear());
        Assert.AreEqual(600, damage.SumTrackedValues());

        damage.Clear();
        Assert.IsEmpty(damage.StoredKeys);
        Assert.AreEqual(0, damage.SumTrackedValues());
        TestContext.WriteLine("Case-insensitive removal, reinsertion and duplicate rejection kept component order and values aligned.");
    }

    [TestMethod]
    public void DamageAndMitigationRequireTheSameDeclaredTypeCatalog()
    {
        var first = new DamageTypeRegistry([new DamageTypeDefinition("impact")]);
        var other = new DamageTypeRegistry([new DamageTypeDefinition("impact")]);
        var schema = new ExpressionSchema<CombatFormulaContext>();
        schema.Seal();
        var formula = NumericFormula<CombatFormulaContext>.Compile("10", schema);
        Assert.ThrowsExactly<ArgumentException>(() => new DamageDefinition(first, "absent", formula));
        var receiver = new BeingDamageAbility { Id = "receiver", DamageTypes = first };
        Assert.ThrowsExactly<ArgumentException>(() => receiver.SetMitigationFormula("absent", formula));
        var mixed = new DamageAbility
        {
            Id = "mixed",
            Damages = [new DamageDefinition(first, "impact", formula), new DamageDefinition(other, "impact", formula)]
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => DamageProfile.From(mixed));

        using var battle = new Battle([new("impact", "10")], receiverTypes: other);
        Assert.ThrowsExactly<InvalidOperationException>(battle.Hit);
        Assert.AreEqual(200, battle.Target.Property.LifePoint);
        TestContext.WriteLine("Undeclared types and mixed catalogs were rejected before damage changed life.");
    }

    [TestMethod]
    public void ProductionFallbackReportsAndMarksTheDamageButStrictModeFails()
    {
        using var strict = new Battle([new("impact", "10 / source.energy", "7")]);
        strict.Source.Property.Energy = 0;
        Assert.ThrowsExactly<ExpressionEvaluationException>(strict.Hit);
        Assert.AreEqual(200, strict.Target.Property.LifePoint);

        var diagnostics = new List<FormulaDiagnostic>();
        var policy = new FormulaEvaluationPolicy(FormulaEvaluationMode.Production, diagnostics.Add);
        using var production = new Battle([new("impact", "10 / source.energy", "7")], policy: policy);
        production.Source.Property.Energy = 0;
        production.Hit();
        Assert.AreEqual(193, production.Target.Property.LifePoint);
        Assert.HasCount(1, diagnostics);
        StringAssert.Contains(diagnostics[0].Error.Message, "Division by zero");
        Assert.IsTrue(production.LifeReductionEvents().Single().Damage.UsedFormulaFallback);
        TestContext.WriteLine("Strict mode rejected division by zero; production reported it and marked the explicit 7-point fallback.");
    }

    [TestMethod]
    public void SettlementFormulaErrorsDoNotUndoAnAlreadyReleasedActivation()
    {
        using var strict = new Battle([new("impact", "10 / source.energy", "7")]);
        CommandGateManager strictGate = strict.ConfigureTriggeredDamageGate(new AbilityChargeConfiguration(1, 1));
        strict.Source.Property.EnergyMax = 100;
        strict.Source.Property.Energy = 10;
        strict.Source.Property.EnergyRecoveryPerSecond = 0;
        var error = Assert.ThrowsExactly<CommittedOperationException>(strict.Hit);
        Assert.IsInstanceOfType<ExpressionEvaluationException>(error.InnerException);
        Assert.AreEqual(0, strictGate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(0m, strict.Source.Property.Energy);
        Assert.AreEqual(200, strict.Target.Property.LifePoint);

        var diagnostics = new List<FormulaDiagnostic>();
        var policy = new FormulaEvaluationPolicy(FormulaEvaluationMode.Production, diagnostics.Add);
        using var production = new Battle([new("impact", "10 / source.energy", "7")], policy: policy);
        CommandGateManager productionGate = production.ConfigureTriggeredDamageGate(new AbilityChargeConfiguration(1, 1));
        production.Source.Property.EnergyMax = 100;
        production.Source.Property.Energy = 10;
        production.Source.Property.EnergyRecoveryPerSecond = 0;
        production.Hit();
        Assert.AreEqual(0, productionGate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(0m, production.Source.Property.Energy);
        Assert.AreEqual(193, production.Target.Property.LifePoint);
        Assert.HasCount(1, diagnostics);
        Assert.IsTrue(production.LifeReductionEvents().Single().Damage.UsedFormulaFallback);
        TestContext.WriteLine("Settlement read energy after release cost; strict mode faulted without undoing release, while production reported and applied the explicit fallback.");
    }

    [TestMethod]
    public void ScaledOverflowUsesOnlyAnExplicitReportedFallback()
    {
        using var strict = new Battle([new("impact", "1e308", "12.5")]);
        Assert.ThrowsExactly<ExpressionEvaluationException>(strict.Hit);
        Assert.AreEqual(200, strict.Target.Property.LifePoint);

        var diagnostics = new List<FormulaDiagnostic>();
        var policy = new FormulaEvaluationPolicy(FormulaEvaluationMode.Production, diagnostics.Add);
        using var production = new Battle([new("impact", "1e308", "12.5")], policy: policy);
        production.Hit();
        Assert.AreEqual(188, production.Target.Property.LifePoint);
        Assert.HasCount(1, diagnostics);
        StringAssert.Contains(diagnostics[0].Error.Message, "representable range");
        Assert.IsTrue(production.LifeReductionEvents().Single().Damage.UsedFormulaFallback);
        TestContext.WriteLine("Unrepresentable scaled damage failed strictly; the diagnosed 12.5 fallback reduced 12 life.");
    }

    [TestMethod]
    public void MitigationFallbackIsMarkedAndAccumulationOverflowFails()
    {
        var diagnostics = new List<FormulaDiagnostic>();
        var policy = new FormulaEvaluationPolicy(FormulaEvaluationMode.Production, diagnostics.Add);
        using var battle = new Battle([new("impact", "10")], policy: policy,
            mitigation: "raw / source.energy", mitigationFallback: "raw");
        battle.Source.Property.Energy = 0;
        battle.Hit();
        Assert.AreEqual(190, battle.Target.Property.LifePoint);
        Assert.HasCount(1, diagnostics);
        Assert.IsTrue(battle.LifeReductionEvents().Single().Damage.UsedFormulaFallback);

        using var zero = new Battle([new("impact", "10")], policy: policy,
            mitigation: "raw / source.energy", mitigationFallback: "0");
        zero.Source.Property.Energy = 0;
        zero.Hit();
        Assert.AreEqual(200, zero.Target.Property.LifePoint);
        Assert.IsTrue(zero.TargetDamageEvents().Single().TotalDamage.UsedFormulaFallback);

        using var overflow = new Battle([new("first", "20000000"), new("second", "20000000")]);
        Assert.ThrowsExactly<OverflowException>(overflow.Hit);
        Assert.AreEqual(200, overflow.Target.Property.LifePoint);
        TestContext.WriteLine("Mitigation fallback was marked; damage accumulation overflow did not alter life.");
    }

    [TestMethod]
    public void PeriodicDamageWithoutAnActorPreservesNullSourceAndExplicitLevel()
    {
        using var battle = new Battle([new("impact", "ability.level * 10")], mitigation: "raw / 2");
        battle.ScheduleDamageWithoutActor(level: 3, count: 3, interval: TimeSpan.FromSeconds(1));
        battle.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.AreEqual(155, battle.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 15, 15, 15 }, battle.LifeReductions());
        foreach (DamageEvent damage in battle.TargetDamageEvents())
        {
            Assert.IsNull(damage.Source);
            Assert.IsNull(damage.TotalDamage.Source);
            Assert.IsNull(damage.Damages.Single().Source);
            Assert.AreSame(battle.Target, damage.TotalDamage.Target);
        }
        foreach (DamageLifeReduceEvent result in battle.LifeReductionEvents())
        {
            Assert.IsNull(result.Source);
            Assert.IsNull(result.Damage.Source);
            Assert.AreSame(battle.Target, result.Damage.Target);
        }
        TestContext.WriteLine("Three actorless level-3 ticks each dealt 15 after mitigation; damage never used the target as source.");
    }

    [TestMethod]
    public void PeriodicDamageStartsANewBudgetOnlyAfterSimulationTimeAdvances()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            totalBudget: 1, mode: TriggerLimitMode.Production, report: diagnostics.Add);
        using var battle = new Battle([new("impact", "10")], triggerFactory: factory);
        var root = (AbilityTriggerChain)factory.Create(battle.Context);
        AbilityTriggerChain prior = root.Append("prior")!;
        battle.ScheduleDamageWithoutActor(
            level: 1, count: 3, interval: TimeSpan.FromSeconds(1), chain: prior);

        battle.AdvanceTo(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { 10, 10 }, battle.LifeReductions());
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.TotalBudget, diagnostics[0].Reason);
        Assert.AreEqual(1, root.TriggerCount);
        ScheduledDamageTickEvent[] ticks = battle.Target.EventReceiver.History
            .OfType<ScheduledDamageTickEvent>().ToArray();
        Assert.HasCount(3, ticks);
        Assert.AreNotSame(prior, ticks[0].TriggerChain);
        Assert.AreNotSame(prior, ticks[1].TriggerChain);
        Assert.AreNotSame(ticks[1].TriggerChain, ticks[2].TriggerChain);
        CollectionAssert.AreEqual(new[] { "prior" }, ticks[0].TriggerChain!.AbilityIds.ToArray());
        Assert.IsEmpty(ticks[1].TriggerChain!.AbilityIds);
        Assert.IsEmpty(ticks[2].TriggerChain!.AbilityIds);
        Assert.IsTrue(ticks.All(tick => tick.TriggerChain!.PeriodicDepth == 1));
        TestContext.WriteLine("The immediate tick inherited the exhausted path and budget; later ticks restarted both while retaining the periodic nesting level.");
    }

    [TestMethod]
    public void LaterPeriodicTicksRestartTheOrdinaryPathDepth()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            totalBudget: 100, mode: TriggerLimitMode.Production, report: diagnostics.Add);
        using var battle = new Battle([new("impact", "10")], triggerFactory: factory);
        var chain = (AbilityTriggerChain)factory.Create(battle.Context);
        for (int i = 0; i < AbilityTriggerChain.MaximumDepth; i++)
            chain = chain.Append("ancestor")!;
        battle.ScheduleDamageWithoutActor(
            level: 1, count: 2, interval: TimeSpan.FromSeconds(1), chain: chain);

        battle.AdvanceTo(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.PathDepth, diagnostics[0].Reason);
        Assert.AreEqual(16, diagnostics[0].Depth);
        ScheduledDamageTickEvent[] ticks = battle.Target.EventReceiver.History
            .OfType<ScheduledDamageTickEvent>().ToArray();
        Assert.HasCount(16, ticks[0].TriggerChain!.AbilityIds);
        Assert.IsEmpty(ticks[1].TriggerChain!.AbilityIds);
        Assert.AreEqual(1, ticks[1].TriggerChain!.PeriodicDepth);
        TestContext.WriteLine("The immediate tick hit the ordinary path limit; the later tick restarted that path without losing its periodic level.");
    }

    [TestMethod]
    public void RepeatedTicksDoNotAccumulateDepthFromEarlierTicks()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production, report: diagnostics.Add);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        var chain = (AbilityTriggerChain)factory.Create(battle.Context);
        for (int i = 0; i < AbilityTriggerChain.MaximumDepth - 1; i++)
            chain = chain.Append("ancestor")!;
        battle.ScheduleDamageWithoutActor(
            level: 1, count: 20, interval: TimeSpan.FromSeconds(1), chain: chain);

        battle.AdvanceTo(TimeSpan.FromSeconds(19));

        Assert.AreEqual(180, battle.Target.Property.LifePoint);
        Assert.HasCount(20, battle.LifeReductions());
        Assert.IsTrue(battle.LifeReductions().All(amount => amount == 1));
        Assert.IsEmpty(diagnostics);
        ScheduledDamageTickEvent[] ticks = battle.Target.EventReceiver.History
            .OfType<ScheduledDamageTickEvent>().ToArray();
        Assert.HasCount(15, ticks[0].TriggerChain!.AbilityIds);
        Assert.IsTrue(ticks.Skip(1).All(tick =>
            tick.TriggerChain!.AbilityIds.Count == 0 && tick.TriggerChain.PeriodicDepth == 1));
        TestContext.WriteLine("Twenty ticks did not accumulate prior trigger paths or increase the periodic nesting level.");
    }

    [TestMethod]
    public void DelayedFirstTickRestartsTheOrdinaryPathAndBudget()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            totalBudget: 16, mode: TriggerLimitMode.Production, report: diagnostics.Add);
        using var battle = new Battle([new("impact", "10")], triggerFactory: factory);
        var chain = (AbilityTriggerChain)factory.Create(battle.Context);
        for (int i = 0; i < AbilityTriggerChain.MaximumDepth; i++)
            chain = chain.Append("ancestor")!;
        battle.ScheduleDamageWithoutActor(
            level: 1, count: 1, interval: TimeSpan.Zero, chain: chain,
            delay: TimeSpan.FromSeconds(1));

        battle.AdvanceTo(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { 10 }, battle.LifeReductions());
        Assert.IsEmpty(diagnostics);
        ScheduledDamageTickEvent tick = battle.Target.EventReceiver.History
            .OfType<ScheduledDamageTickEvent>().Single();
        Assert.IsEmpty(tick.TriggerChain!.AbilityIds);
        Assert.AreEqual(1, tick.TriggerChain.PeriodicDepth);
        Assert.AreEqual(16, chain.TriggerCount);
        TestContext.WriteLine("A first tick after simulation time advanced received a fresh ordinary path and budget.");
    }

    [TestMethod]
    public void ChildPeriodicEffectsStopAtTheSeventeenthNestingLevel()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var production = new TriggerChainFactory(mode: TriggerLimitMode.Production, report: diagnostics.Add);
        using var battle = new Battle([new("impact", "1")], triggerFactory: production);
        battle.Target.EventReceiver.AddHandler(new NestedPeriodicOnDamage(battle.Profile(1)));
        battle.ScheduleDamageWithoutActor(level: 1, count: 2, interval: TimeSpan.FromDays(1));

        battle.AdvanceTo(TimeSpan.FromSeconds(15));

        CollectionAssert.AreEqual(Enumerable.Repeat(1, 16).ToArray(), battle.LifeReductions());
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.PeriodicDepth, diagnostics[0].Reason);
        Assert.AreEqual(16, diagnostics[0].Depth);
        ScheduledDamageTickEvent[] ticks = battle.Target.EventReceiver.History
            .OfType<ScheduledDamageTickEvent>().ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 16).ToArray(),
            ticks.Select(tick => tick.TriggerChain!.PeriodicDepth).ToArray());
        Assert.IsTrue(ticks.Skip(1).All(tick => tick.TriggerChain!.AbilityIds.Count == 0));

        using var strict = new Battle([new("impact", "1")]);
        strict.Target.EventReceiver.AddHandler(new NestedPeriodicOnDamage(strict.Profile(1)));
        strict.ScheduleDamageWithoutActor(level: 1, count: 2, interval: TimeSpan.FromDays(1));
        var error = Assert.ThrowsExactly<TriggerLimitExceededException>(
            () => strict.AdvanceTo(TimeSpan.FromSeconds(15)));
        Assert.AreEqual(TriggerLimitReason.PeriodicDepth, error.Diagnostic.Reason);
        CollectionAssert.AreEqual(Enumerable.Repeat(1, 16).ToArray(), strict.LifeReductions());
        TestContext.WriteLine("Sixteen time-separated nested periodic effects executed; the seventeenth was reported or threw according to mode.");
    }

    [TestMethod]
    public void PeriodicCapacityIsReservedBeforeTheFirstTickAndIsIsolatedPerContext()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add, maxActivePeriodicEffects: 2);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        using var other = new Battle([new("impact", "1")], triggerFactory: factory);
        using var cancellation = new CancellationTokenSource();
        PeriodicDamageRequestEvent first = battle.ScheduleDamageWithoutActor(
            1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10), cancellation: cancellation);
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        PeriodicDamageRequestEvent rejected = battle.ScheduleDamageWithoutActor(
            1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        other.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        battle.AdvanceTo(TimeSpan.Zero);
        other.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(other.Context));
        Assert.HasCount(2, battle.Target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>());
        PeriodicEffectRejectedEvent failure = battle.Target.EventReceiver.History
            .OfType<PeriodicEffectRejectedEvent>().Single();
        Assert.AreEqual(rejected.CommandEventId, failure.CommandEventId);
        Assert.AreEqual(PeriodicEffectRejectionReason.Capacity, failure.Reason);
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.ActivePeriodicEffects, diagnostics[0].Reason);
        Assert.AreEqual(2, diagnostics[0].ActivePeriodicEffects);
        Assert.IsEmpty(battle.LifeReductions());

        cancellation.Cancel();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(2, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.HasCount(3, battle.Target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>());
        Assert.AreEqual(first.CommandEventId, battle.Target.EventReceiver.History
            .OfType<PeriodicEffectStartedEvent>().First().CommandEventId);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(other.Context));
        TestContext.WriteLine("Two pending periodic effects filled one match; rejection was explicit, cancellation reopened capacity, and another match was unaffected.");
    }

    [TestMethod]
    public void CompletionExpiryAndQueueClearingReleasePeriodicCapacity()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1));
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        CollectionAssert.AreEqual(new[] { 1 }, battle.LifeReductions());
        battle.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        CollectionAssert.AreEqual(new[] { 1, 1 }, battle.LifeReductions());

        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1),
            delay: TimeSpan.FromSeconds(10), expiresAt: TimeSpan.FromSeconds(3));
        battle.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.HasCount(2, battle.LifeReductions());

        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        battle.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.Context.ClearPendingEvents();
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        TestContext.WriteLine("Capacity was released after the final tick, at expiry, and when pending match work was cleared.");
    }

    [TestMethod]
    public void ReplacingAPeriodicEffectUsesNetCapacityAndOldExpiryCannotRemoveTheNewEffect()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        PeriodicDamageRequestEvent first = battle.ScheduleDamageWithoutActor(
            1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(2), expiresAt: TimeSpan.FromSeconds(3));
        battle.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle original = battle.Started(first).Effect;
        PeriodicDamageRequestEvent replacement = battle.ScheduleDamageWithoutActor(
            1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(4),
            expiresAt: TimeSpan.FromSeconds(8), replacement: original);
        battle.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle current = battle.Started(replacement).Effect;

        Assert.AreNotSame(original, current);
        Assert.AreNotEqual(original.Id, current.Id);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.IsEmpty(battle.LifeReductions());
        battle.AdvanceTo(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 1, 1 }, battle.LifeReductions());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        TestContext.WriteLine("Replacement succeeded at the limit; the old delayed tick and expiry did not affect the new effect.");
    }

    [TestMethod]
    public async Task CancellationIsAppliedByTheContextOwnerBeforePublishingTheNewCount()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        using var cancellation = new CancellationTokenSource();
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1),
            delay: TimeSpan.FromSeconds(10), cancellation: cancellation);
        battle.AdvanceTo(TimeSpan.Zero);

        int published = await Task.Run(() =>
        {
            cancellation.Cancel();
            return factory.GetActivePeriodicEffectCount(battle.Context);
        });
        Assert.AreEqual(1, published);
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.AdvanceTo(TimeSpan.FromSeconds(11));
        Assert.IsEmpty(battle.LifeReductions());
        TestContext.WriteLine("Cross-thread cancellation posted work; the published count changed only when the context owner processed it.");
    }

    [TestMethod]
    public void ReplacingWithAnotherContextsEffectDoesNotChangeEitherContext()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var first = new Battle([new("impact", "1")], triggerFactory: factory);
        using var second = new Battle([new("impact", "1")], triggerFactory: factory);
        PeriodicDamageRequestEvent request = first.ScheduleDamageWithoutActor(
            1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(2));
        first.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle original = first.Started(request).Effect;
        second.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), replacement: original);

        Assert.ThrowsExactly<InvalidOperationException>(() => second.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(first.Context));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(second.Context));
        first.AdvanceTo(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(new[] { 1, 1 }, first.LifeReductions());
        TestContext.WriteLine("A cross-context replacement failed without altering the original effect or reserving a slot in the other context.");
    }

    [TestMethod]
    public void ReplacementCancellationCallbackFailureReportsTheCommittedOperation()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        CancellationTokenRegistration registration = default;
        bool registered = false;
        battle.Context.EventQueued += ev =>
        {
            if (!registered && ev is AbilityLifecycleEvent { RemainingTicks: > 0 } lifecycle)
            {
                registered = true;
                registration = lifecycle.CancellationToken.Register(
                    () => throw new InvalidOperationException("periodic cancellation observer failed"));
            }
        };
        try
        {
            PeriodicDamageRequestEvent first = battle.ScheduleDamageWithoutActor(
                1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(2));
            battle.AdvanceTo(TimeSpan.Zero);
            Assert.IsTrue(registered);
            PeriodicEffectHandle original = battle.Started(first).Effect;
            battle.ScheduleDamageWithoutActor(
                1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(4), replacement: original);

            var failure = Assert.ThrowsExactly<CommittedOperationException>(() => battle.AdvanceTo(TimeSpan.Zero));
            Assert.AreNotEqual(Guid.Empty, failure.OperationId);
            StringAssert.Contains(failure.InnerException!.ToString(), "periodic cancellation observer failed");
            Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
            Assert.HasCount(1, battle.Target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>());
            TestContext.WriteLine("A cancellation observer failed after replacement committed; the context reported the committed operation rather than claiming rollback.");
        }
        finally
        {
            registration.Unregister();
        }
    }

    [TestMethod]
    public void DeathAndRetirementReleasePendingPeriodicEffects()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var death = new Battle([new("impact", "250")], triggerFactory: factory);
        death.InstallOnTarget(new DeathAbility { Id = "death" });
        death.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        death.AdvanceTo(TimeSpan.Zero);
        death.Hit();
        Assert.IsFalse(death.Target.IsAlive);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(death.Context));
        DeathResolvedEvent resolved = death.Target.EventReceiver.History.OfType<DeathResolvedEvent>().Single();
        DeathEvent pending = death.Target.EventReceiver.History.OfType<DeathEvent>().Single();
        Assert.AreSame(pending.DamageAbility, resolved.DamageAbility);

        using var retired = new Battle([new("impact", "1")], triggerFactory: factory);
        retired.Target.EventReceiver.AddHandler(new UnitRetirementHandler());
        retired.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        retired.AdvanceTo(TimeSpan.Zero);
        retired.Context.Dispatch(retired.Context.Factory.NewEvent<UnitRetirementRequestEvent>() with
        {
            Unit = retired.Target, Receiver = retired.Target.EventReceiver
        }).ToList();
        retired.AdvanceTo(TimeSpan.Zero);
        Assert.IsTrue(retired.Target.IsRetired);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(retired.Context));
        retired.AdvanceTo(TimeSpan.FromSeconds(11));
        Assert.IsEmpty(retired.LifeReductions());
        TestContext.WriteLine("Confirmed death and retirement each released their pending periodic effect without applying future ticks.");
    }

    [TestMethod]
    public void AFirstTickAtExpiryDoesNotExecute()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1),
            delay: TimeSpan.FromSeconds(3), expiresAt: TimeSpan.FromSeconds(3));
        battle.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.IsEmpty(battle.LifeReductions());
        TestContext.WriteLine("Expiry at the first tick's timestamp released the slot without executing an expired effect.");
    }

    [TestMethod]
    public void InvalidReplacementAndStrictCapacityFailureLeaveTheExistingEffectIntact()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TriggerChainFactory(maxActivePeriodicEffects: 0));
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        PeriodicDamageRequestEvent first = battle.ScheduleDamageWithoutActor(
            1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(2));
        battle.AdvanceTo(TimeSpan.Zero);
        PeriodicEffectHandle existing = battle.Started(first).Effect;
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.Zero, replacement: existing);
        Assert.ThrowsExactly<InvalidOperationException>(() => battle.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));

        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1));
        var error = Assert.ThrowsExactly<TriggerLimitExceededException>(() => battle.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(TriggerLimitReason.ActivePeriodicEffects, error.Diagnostic.Reason);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.AdvanceTo(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(new[] { 1, 1 }, battle.LifeReductions());
        TestContext.WriteLine("Invalid replacement and strict capacity failure did not cancel or alter the prior effect.");
    }

    [TestMethod]
    public void PeriodicCommandRejectsAtCapacityWithoutSpendingResourceOrCooldown()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add, maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        using var cancellation = new CancellationTokenSource();
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1),
            delay: TimeSpan.FromSeconds(10), cancellation: cancellation);
        battle.AdvanceTo(TimeSpan.Zero);
        CommandGateManager gate = battle.ConfigurePeriodicCommand();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        CommandEvent rejected = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);

        CommandExecutionEvent failure = battle.CommandResult(rejected);
        Assert.AreEqual(CommandExecutionStatus.Failure, failure.Status);
        Assert.AreEqual(CommandExecutionFailureReason.PeriodicEffectLimit, failure.FailureReason);
        Assert.AreEqual(50m, battle.Source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, gate.NextReadyTime);
        Assert.IsFalse(gate.HasPendingReservation);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.HasCount(1, diagnostics);
        Assert.IsEmpty(battle.LifeReductions());

        cancellation.Cancel();
        CommandEvent accepted = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(CommandExecutionStatus.Success, battle.CommandResult(accepted).Status);
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.AreEqual(TimeSpan.FromSeconds(5), gate.NextReadyTime);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.AdvanceTo(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { 1, 1 }, battle.LifeReductions());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        TestContext.WriteLine("A full quota rejected the actual command without resource or cooldown; canceling the old effect allowed a successful two-tick cast.");
    }

    [TestMethod]
    public void FailedResourceCheckReleasesThePreparedPeriodicReservation()
    {
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        battle.ScheduleDamageWithoutActor(1, 1, TimeSpan.Zero);
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        CommandGateManager gate = battle.ConfigurePeriodicCommand();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 5;

        CommandEvent command = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(CommandExecutionStatus.Failure, battle.CommandResult(command).Status);
        Assert.AreEqual(CommandExecutionFailureReason.ResourceInsufficient, battle.CommandResult(command).FailureReason);
        Assert.AreEqual(5m, battle.Source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, gate.NextReadyTime);
        Assert.IsFalse(gate.HasPendingReservation);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.HasCount(1, battle.LifeReductions());
        TestContext.WriteLine("An insufficient-resource release discarded its prepared periodic slot without starting cooldown.");
    }

    [TestMethod]
    public void TwoTriggersOfTheSameCommandCreateIndependentActivations()
    {
        using var battle = new Battle([new("impact", "1")]);
        CommandGateManager gate = battle.ConfigurePeriodicCommand(TimeSpan.Zero);
        battle.AddCommandTrigger();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        CommandEvent command = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);

        UnitActionGateRequestEvent[] activations = battle.Source.EventReceiver.History
            .OfType<UnitActionGateRequestEvent>().Where(ev => ReferenceEquals(ev.Origin, command)).ToArray();
        Assert.HasCount(2, activations);
        Assert.AreNotEqual(activations[0].ActivationId, activations[1].ActivationId);
        CommandExecutionEvent[] results = battle.Source.EventReceiver.History
            .OfType<CommandExecutionEvent>().Where(ev => ReferenceEquals(ev.Command, command)).ToArray();
        Assert.HasCount(2, results);
        Assert.IsTrue(results.All(ev => ev.Status == CommandExecutionStatus.Success));
        Assert.AreEqual(30m, battle.Source.Property.Energy);
        Assert.IsFalse(gate.HasPendingReservation);
        battle.AdvanceTo(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1 }, battle.LifeReductions());
        TestContext.WriteLine("Two matching triggers of one command produced two independently identified releases and four ticks.");
    }

    [TestMethod]
    public void TwoAcceptedTriggersCompeteAtReleaseForActualCooldown()
    {
        using var battle = new Battle([new("impact", "1")]);
        CommandGateManager gate = battle.ConfigurePeriodicCommand();
        battle.AddCommandTrigger();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        CommandEvent command = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);

        CommandExecutionEvent[] results = battle.Source.EventReceiver.History
            .OfType<CommandExecutionEvent>().Where(ev => ReferenceEquals(ev.Command, command)).ToArray();
        Assert.HasCount(2, results);
        Assert.AreEqual(1, results.Count(ev => ev.Status == CommandExecutionStatus.Success));
        Assert.AreEqual(1, results.Count(ev => ev.FailureReason == CommandExecutionFailureReason.CoolingDown));
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.AreEqual(TimeSpan.FromSeconds(5), gate.NextReadyTime);
        Assert.IsFalse(gate.HasPendingReservation);
        battle.AdvanceTo(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { 1, 1 }, battle.LifeReductions());
        TestContext.WriteLine("Two trigger activations reached release; the configured cooldown rejected one without charging energy or creating ticks.");
    }

    [TestMethod]
    public void TwoTriggeredReleasesCompeteForTheLastCharge()
    {
        var factory = new TriggerChainFactory();
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        CommandGateManager gate = battle.ConfigurePeriodicCommand(TimeSpan.Zero, new AbilityChargeConfiguration(1, 1));
        battle.AddCommandTrigger();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        CommandEvent command = battle.CastPeriodicCommand();
        battle.AdvanceTo(TimeSpan.Zero);

        CommandExecutionEvent[] results = battle.Source.EventReceiver.History
            .OfType<CommandExecutionEvent>().Where(ev => ReferenceEquals(ev.Command, command)).ToArray();
        Assert.HasCount(2, results);
        Assert.AreEqual(1, results.Count(ev => ev.Status == CommandExecutionStatus.Success));
        Assert.AreEqual(1, results.Count(ev => ev.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        battle.AdvanceTo(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { 1, 1 }, battle.LifeReductions());
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(battle.Context));
        TestContext.WriteLine("Two real trigger chains competed for one charge; the rejected release spent no energy and left no periodic reservation.");
    }

    [TestMethod]
    public void ProjectileHitTriggeredDamageUsesItsOwnChargeGate()
    {
        using var battle = new Battle([new("impact", "3")]);
        CommandGateManager gate = battle.ConfigureTriggeredDamageGate(new AbilityChargeConfiguration(1, 1));
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        battle.Hit();
        battle.Hit();

        CollectionAssert.AreEqual(new[] { 3 }, battle.LifeReductions());
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        TestContext.WriteLine("A hit-triggered damage ability consumed its own last charge once and explicitly rejected the next hit.");
    }

    [TestMethod]
    public void DamageRespondingToAnotherCommandUsesAnIndependentActivation()
    {
        using var battle = new Battle([new("impact", "3")]);
        CommandGateManager gate = battle.ConfigureTriggeredDamageGate(new AbilityChargeConfiguration(1, 1));
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        CommandEvent command = battle.ObserveCommandWithDamage();

        CollectionAssert.AreEqual(new[] { 3 }, battle.LifeReductions());
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(battle.Source.EventReceiver.History.OfType<CommandExecutionEvent>()
            .Where(result => ReferenceEquals(result.Command, command)));
        AbilityActivationResultEvent result = battle.Source.EventReceiver.History
            .OfType<AbilityActivationResultEvent>().Single();
        Assert.AreEqual(CommandExecutionStatus.Success, result.Status);
        TestContext.WriteLine("Damage observing another command committed its own gated activation, not the original command.");
    }

    [TestMethod]
    public void ProjectileRespondingToAnotherCommandUsesAnIndependentActivation()
    {
        using var battle = new Battle([new("impact", "3")]);
        ProjectileAbility ability = battle.ConfigureTriggeredProjectileGate();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        CommandEvent command = battle.ObserveCommandWithProjectile(ability);

        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<ProjectileEvent>()
            .Where(projectile => projectile.AbilityId == ability.Id));
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.AreEqual(0, ability.GateManager!.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(battle.Source.EventReceiver.History.OfType<CommandExecutionEvent>()
            .Where(result => ReferenceEquals(result.Command, command)));
        AbilityActivationResultEvent result = battle.Source.EventReceiver.History
            .OfType<AbilityActivationResultEvent>().Single();
        Assert.AreEqual(ability.RuntimeInstanceId, result.AbilityInstanceId);
        Assert.AreEqual(CommandExecutionStatus.Success, result.Status);
        TestContext.WriteLine("A projectile observing another command used its own charge gate and activation result.");
    }

    [TestMethod]
    public void TriggeredPeriodicDamageDoesNotSpendWhenCapacityIsExhausted()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production,
            report: diagnostics.Add, maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "3")], triggerFactory: factory);
        CommandGateManager gate = battle.ConfigureTriggeredDamageGate(
            new AbilityChargeConfiguration(1, 1), periodic: true);
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        battle.AdvanceTo(TimeSpan.Zero);

        battle.Hit();

        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(50m, battle.Source.Property.Energy);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.HasCount(1, diagnostics);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.PeriodicEffectLimit));
        Assert.IsEmpty(battle.LifeReductions());
        TestContext.WriteLine("A rejected periodic reservation left energy and charges untouched and reported the capacity limit.");
    }

    [TestMethod]
    public void TriggeredPeriodicDamageChecksEnergyBeforePreparingEffects()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production,
            report: diagnostics.Add, maxActivePeriodicEffects: 1);
        using var battle = new Battle([new("impact", "3")], triggerFactory: factory);
        CommandGateManager gate = battle.ConfigureTriggeredDamageGate(
            new AbilityChargeConfiguration(1, 1), periodic: true);
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 9;
        battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromSeconds(10));
        battle.AdvanceTo(TimeSpan.Zero);

        battle.Hit();

        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(9m, battle.Source.Property.Energy);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.IsEmpty(diagnostics);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.ResourceInsufficient));
        TestContext.WriteLine("Insufficient energy rejected the release before the unrelated full periodic quota was consulted.");
    }

    [TestMethod]
    public void TriggeredPeriodicDamageSamplesAbilityAtReleaseAndUnitAtEachTick()
    {
        using var battle = new Battle([new("impact", "ability.level * 10 + source.level")]);
        battle.ConfigureTriggeredDamageGate(new AbilityChargeConfiguration(1, 1),
            periodic: true, windup: TimeSpan.FromSeconds(2));
        battle.Source.Property.EnergyRecoveryPerSecond = 0;
        battle.Hit();
        battle.SetDamageLevel(2);
        battle.AdvanceTo(TimeSpan.FromSeconds(2));

        battle.SetDamageLevel(3);
        battle.Source.SetLevel(4);
        battle.AdvanceTo(TimeSpan.FromSeconds(3));
        battle.Source.SetLevel(5);
        battle.AdvanceTo(TimeSpan.FromSeconds(4));

        CollectionAssert.AreEqual(new[] { 24, 25 }, battle.LifeReductions());
        TestContext.WriteLine("The release captured ability level 2; successive ticks read source levels 4 and 5.");
    }

    [TestMethod]
    public void TriggeredReleaseReportsObserverFailureAfterItsStateCommitted()
    {
        var factory = new TriggerChainFactory();
        using var battle = new Battle([new("impact", "3")], triggerFactory: factory);
        CommandGateManager gate = battle.ConfigureTriggeredDamageGate(
            new AbilityChargeConfiguration(1, 1), periodic: true);
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;
        battle.Context.EventQueued += ev =>
        {
            if (ev is AbilityChargesChangedEvent { Charges.Available: 0 })
                throw new InvalidOperationException("charge notification observer failed");
        };

        var failure = Assert.ThrowsExactly<CommittedOperationException>(() => battle.Hit());

        Assert.AreNotEqual(Guid.Empty, failure.OperationId);
        StringAssert.Contains(failure.InnerException!.Message, "charge notification observer failed");
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.AreEqual(1, factory.GetActivePeriodicEffectCount(battle.Context));
        TestContext.WriteLine("A post-commit notification failure reported the committed activation without rolling back its periodic reservation.");
    }

    [TestMethod]
    public void TriggeredProjectileUsesItsOwnChargeGate()
    {
        using var battle = new Battle([new("impact", "3")]);
        ProjectileAbility ability = battle.ConfigureTriggeredProjectileGate();
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;

        battle.Hit();
        battle.Hit();

        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<ProjectileEvent>()
            .Where(projectile => projectile.AbilityId == ability.Id));
        Assert.AreEqual(0, ability.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(40m, battle.Source.Property.Energy);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.AbilityInstanceId == ability.RuntimeInstanceId &&
                result.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        TestContext.WriteLine("A projectile activated by a hit consumed exactly one charge and rejected the second activation.");
    }

    [TestMethod]
    public void TriggeredProjectileRevalidatesItsTargetAfterWindup()
    {
        using var battle = new Battle([new("impact", "3")]);
        ProjectileAbility ability = battle.ConfigureTriggeredProjectileGate(TimeSpan.FromSeconds(2));
        battle.Source.Property.EnergyMax = 100;
        battle.Source.Property.Energy = 50;
        battle.Source.Property.EnergyRecoveryPerSecond = 0;
        battle.InstallOnTarget(new DeathAbility { Id = "death" });

        battle.Hit();
        battle.Target.Property.LifePoint = 0;
        battle.Context.Dispatch(PendingDeathPublicationEvent.FromAttempt(battle.Context.Factory.NewEvent<DeathEvent>() with
        {
            CommandEventId = Guid.NewGuid(), DamageAbility = battle.Profile(1),
            Victim = battle.Target, Killer = battle.Source, LifePointReduce = 1,
            Receiver = battle.Target.EventReceiver
        })).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.IsFalse(battle.Target.IsAlive);
        battle.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.IsEmpty(battle.Source.EventReceiver.History.OfType<ProjectileEvent>()
            .Where(projectile => projectile.AbilityId == ability.Id));
        Assert.AreEqual(1, ability.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(50m, battle.Source.Property.Energy);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.AbilityInstanceId == ability.RuntimeInstanceId &&
                result.FailureReason == CommandExecutionFailureReason.TargetNotExist));
        TestContext.WriteLine("A hit-triggered projectile rejected a target lost during windup without spending energy or charges.");
    }

    [TestMethod]
    public void UngatedTriggeredCombatAbilitiesExplicitlyRejectADeadTarget()
    {
        using var battle = new Battle([new("impact", "3")]);
        ProjectileAbility projectile = battle.ConfigureTriggeredProjectileGate(gated: false);
        battle.InstallOnTarget(new DeathAbility { Id = "death" });
        battle.Context.Dispatch(PendingDeathPublicationEvent.FromAttempt(battle.Context.Factory.NewEvent<DeathEvent>() with
        {
            CommandEventId = Guid.NewGuid(), DamageAbility = battle.Profile(1),
            Victim = battle.Target, Killer = battle.Source, LifePointReduce = 1,
            Receiver = battle.Target.EventReceiver
        })).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        Assert.IsFalse(battle.Target.IsAlive);

        battle.Hit();

        Assert.IsEmpty(battle.Source.EventReceiver.History.OfType<ProjectileEvent>()
            .Where(item => item.AbilityId == projectile.Id));
        Assert.IsEmpty(battle.LifeReductions());
        Assert.HasCount(2, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.TargetNotExist));
        TestContext.WriteLine("Ungated damage and projectile activations each explicitly rejected the dead target.");
    }

    [TestMethod]
    public void DefaultPeriodicCapacityAccepts4096AndRejectsTheNextEffect()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production, report: diagnostics.Add);
        using var battle = new Battle([new("impact", "1")], triggerFactory: factory);
        for (int i = 0; i < 4097; i++)
            battle.ScheduleDamageWithoutActor(1, 2, TimeSpan.FromSeconds(1), delay: TimeSpan.FromHours(1));

        battle.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(4096, factory.GetActivePeriodicEffectCount(battle.Context));
        Assert.HasCount(4096, battle.Target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>());
        Assert.HasCount(1, battle.Target.EventReceiver.History.OfType<PeriodicEffectRejectedEvent>());
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(4096, diagnostics[0].ActivePeriodicEffects);
        Assert.IsEmpty(battle.LifeReductions());
        TestContext.WriteLine("Exactly 4096 independent pending periodic effects were accepted; the next was rejected before its first tick.");
    }

    [TestMethod]
    public void ActorlessDamageReportsNullKillerAndRejectsInvalidLevelOrInterval()
    {
        using var battle = new Battle([new("impact", "110")]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => battle.Profile(0));
        Damage mismatched = battle.Profile(1).Calculate(battle.Source, battle.Target, TimeSpan.Zero);
        var actorless = new DamageEvent { CommandEventId = Guid.NewGuid(), Source = null, Target = battle.Target };
        Assert.ThrowsExactly<InvalidOperationException>(() => actorless.AppendDamage(mismatched));
        battle.ScheduleDamageWithoutActor(level: 1, count: 2, interval: TimeSpan.FromSeconds(1));
        battle.AdvanceTo(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(new[] { 110, 110 }, battle.LifeReductions());
        Assert.AreEqual(0, battle.Target.Property.LifePoint);
        DeathEvent death = battle.Target.EventReceiver.History.OfType<DeathEvent>().Single();
        Assert.IsNull(death.Killer);
        Assert.AreSame(battle.Target, death.Victim);

        using var invalid = new Battle([new("impact", "10")]);
        invalid.ScheduleDamageWithoutActor(level: 1, count: 2, interval: TimeSpan.Zero);
        Assert.ThrowsExactly<InvalidOperationException>(() => invalid.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(200, invalid.Target.Property.LifePoint);
        TestContext.WriteLine("Actorless lethal damage retained a null killer; zero level and zero repeat interval failed explicitly.");
    }

    [TestMethod]
    public void OneDamageEventCannotMergeDifferentEffectSnapshots()
    {
        using var battle = new Battle([new("impact", "10")]);
        DamageProfile first = battle.Profile(1);
        DamageProfile second = battle.Profile(2);
        var ev = new DamageEvent
        {
            CommandEventId = Guid.NewGuid(), Source = battle.Source, Target = battle.Target
        };
        ev.AppendDamage(first.Calculate(battle.Source, battle.Target, TimeSpan.Zero));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ev.AppendDamage(second.Calculate(battle.Source, battle.Target, TimeSpan.Zero)));

        Assert.HasCount(1, ev.Damages);
        Assert.AreSame(first.Ability, ev.AbilitySnapshot);
        Assert.AreEqual(first.Id, ev.DamageAbilityId);
        TestContext.WriteLine("A damage event rejected another effect snapshot without changing its existing amount or source.");
    }

    [TestMethod]
    public void DeferredLethalDamageKeepsItsOwnCausalityRatherThanTheReplayRequest()
    {
        using var battle = new Battle([new("impact", "210")]);
        using var settlement = new DeferredCombatImpacts(battle.Target);
        DamageProfile profile = battle.Profile(1);
        var damage = battle.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null,
            Target = battle.Target, Receiver = battle.Target.EventReceiver
        };
        damage.AppendDamage(profile.Calculate(null, battle.Target, TimeSpan.Zero));
        battle.Context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        battle.AdvanceTo(TimeSpan.Zero);
        settlement.BeginReplay();
        Assert.AreSame(damage, settlement.TakeNext());

        var replay = battle.Context.Factory.NewEvent<DeferredDamageEvent>() with
        { Settlement = settlement, Damage = damage, Receiver = battle.Target.EventReceiver };
        battle.Context.Dispatch(replay).ToList();
        battle.AdvanceTo(TimeSpan.Zero);

        DeathEvent death = battle.Target.EventReceiver.History.OfType<DeathEvent>().Single();
        Assert.AreNotSame(damage.Causality, replay.Causality);
        Assert.AreSame(damage.Causality, death.Causality);
        Assert.AreSame(damage.Causality, battle.LifeReductionEvents().Single().Causality);
        TestContext.WriteLine("Deferred damage and its death retained the original damage chain, not the independent replay request.");
    }

    private sealed record ScopedDamageRequestEvent : Event;

    private sealed class ScopedDamageEmitter(Unit source, Unit target, DamageProfile profile) : IHandler
    {
        public string Id => nameof(ScopedDamageEmitter);
        public DamageEvent? Damage { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnRequest(ScopedDamageRequestEvent ev)
        {
            Damage = ev.Context.Factory.NewEvent<DamageEvent>() with
            { CommandEventId = Guid.NewGuid(), Source = source, Target = target, Receiver = source.EventReceiver };
            Damage.AppendDamage(profile.Calculate(source, target, TimeSpan.Zero));
            yield return DamageRequestEvent.FromDamage(Damage);
        }
    }

    private sealed class DeferredReplayRecorder(Unit target) : IHandler
    {
        public string Id => nameof(DeferredReplayRecorder);
        public List<DeferredDamageReplayCompletedEvent> Events { get; } = [];
        public int? LifeAtCompletion { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnCompletion(DeferredDamageReplayCompletedEvent ev)
        {
            Events.Add(ev);
            LifeAtCompletion = target.Property.LifePoint;
            return [];
        }
    }

    private sealed record DamageInput(string Type, string Formula, string? Fallback = null, int Priority = 0);

    private sealed class NestedPeriodicOnDamage(DamageProfile profile) : IHandler
    {
        public string Id => nameof(NestedPeriodicOnDamage);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnDamage(DamageLifeReduceEvent ev)
        {
            if (ev.Target == null || ev.Receiver != ev.Target.EventReceiver)
                yield break;
            yield return ev.Context.Factory.NewEvent<PeriodicDamageRequestEvent>() with
            {
                CommandEventId = ev.CommandEventId, Source = ev.Source, Target = ev.Target,
                TickCount = 2, TickInterval = TimeSpan.FromDays(1), Damage = profile,
                Receiver = ev.Target.EventReceiver, FirstTickDelay = TimeSpan.FromSeconds(1)
            };
        }
    }

    private sealed class ChangeLevelsBeforeDamage(Unit source, Unit target) : IHandler
    {
        public string Id => nameof(ChangeLevelsBeforeDamage);
        public int Priority => -10;
        public bool SawPendingDamage { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnDamage(DamageEvent ev)
        {
            SawPendingDamage = ev.HasDamage && !ev.IsDamageCalculated;
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = ev.TotalDamage);
            source.SetLevel(4);
            target.SetLevel(3);
            yield break;
        }
    }

    private sealed class LifeReductionRecorder : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        public List<AbilityTriggerInvokeEvent> Invocations { get; } = [];

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId == Id) Invocations.Add(ev);
            yield break;
        }
    }

    private static TriggerHandler LifeTrigger(string targetId, TriggerSourceScope scope) => new()
    {
        Id = $"{targetId}-trigger",
        TargetAbilityId = targetId,
        Config = new AbilityTriggerConfig
        {
            Type = TriggerType.TargetLifeReduce,
            EventSourceScope = scope
        }
    };

    private static TriggerHandler DamageTrigger(string targetId, TriggerType type, TriggerSourceScope scope) => new()
    {
        Id = $"{targetId}-trigger",
        TargetAbilityId = targetId,
        Config = new AbilityTriggerConfig
        {
            Type = type, EventSourceScope = scope, SourceAbilityIds = ["payload"]
        }
    };

    private static TriggerHandler DeathTrigger(
        string targetId, TriggerSourceScope scope, DeathTriggerPhase phase = DeathTriggerPhase.Confirmed) => new()
    {
        Id = $"{targetId}-trigger",
        TargetAbilityId = targetId,
        Config = new AbilityTriggerConfig
        {
            Type = TriggerType.Death,
            DeathPhase = phase,
            EventSourceScope = scope
        }
    };

    private sealed class CancelPendingDeathResponse : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        public int Count { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId == Id && ev.UpstreamEvent is DeathEvent death)
            {
                Count++;
                Assert.IsNotNull(death.CancellationTokenSource);
                death.CancellationTokenSource.Cancel();
            }
            yield break;
        }
    }

    private sealed class CancelLifeReduction : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnLifeReduction(DamageLifeReduceEvent ev)
        {
            Assert.IsNotNull(ev.CancellationTokenSource);
            ev.CancellationTokenSource.Cancel();
            yield break;
        }
    }

    private sealed class CancelDamageRequest : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDamage(DamageEvent ev)
        {
            Assert.IsNotNull(ev.CancellationTokenSource);
            ev.CancellationTokenSource.Cancel();
            yield break;
        }
    }

    private sealed class CancelDeathAttempt : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnDeath(DeathEvent ev)
        {
            Assert.IsNotNull(ev.CancellationTokenSource);
            ev.CancellationTokenSource.Cancel();
            yield break;
        }
    }

    private sealed class RecordLifeReduction : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        public int Count { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnLifeReduction(DamageLifeReduceEvent ev)
        {
            Count++;
            yield break;
        }
    }

    private sealed class Battle : IDisposable
    {
        private readonly Context context;
        private readonly DamageAbility ability;

        public Battle(
            IReadOnlyList<DamageInput> inputs, int sourceLevel = 1, int targetLevel = 1,
            FormulaEvaluationPolicy? policy = null, string? mitigation = null,
            string? mitigationFallback = null, double armor = 0,
            DamageTypeRegistry? receiverTypes = null, TriggerChainFactory? triggerFactory = null,
            IReadOnlySet<string>? sourceTags = null, IReadOnlySet<string>? targetTags = null)
        {
            context = new Context(causalityFactory: triggerFactory ?? new TriggerChainFactory());
            context.Logger.Output = null;
            Source = CreateUnit(context, "source", sourceLevel, sourceTags);
            Target = CreateUnit(context, "target", targetLevel, targetTags);
            context.AddEventReceiver(Source.EventReceiver);
            context.AddEventReceiver(Target.EventReceiver);

            var schema = new ExpressionSchema<CombatFormulaContext>();
            schema.RegisterNumber("ability.level", value => value.AbilityLevel);
            schema.RegisterNumber("source.level", value => value.SourceProperty?.Level, nullable: true);
            schema.RegisterNumber("source.energy", value => (double?)value.SourceProperty?.Energy, nullable: true);
            schema.RegisterNumber("target.level", value => value.TargetProperty?.Level, nullable: true);
            schema.RegisterNumber("raw", value => value.Raw, nullable: true);
            schema.RegisterNumber("armor", value => value.Target == null ? null : armor, nullable: true);
            schema.Seal();

            var damageTypes = new DamageTypeRegistry(inputs
                .Select(input => input.Type)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(type => new DamageTypeDefinition(type)));
            ability = new DamageAbility
            {
                Id = "payload",
                FormulaPolicy = policy ?? FormulaEvaluationPolicy.Strict,
                Damages = inputs.Select(input => new DamageDefinition(damageTypes, input.Type,
                    NumericFormula<CombatFormulaContext>.Compile(input.Formula, schema, input.Fallback),
                    input.Priority)).ToList()
            };
            var trigger = new TriggerHandler
            {
                Id = "payload-trigger",
                TargetAbilityId = ability.Id,
                TriggerType = TriggerType.ProjectileHit,
                SourceAbilityIds = ["delivery"]
            };
            var receiver = new BeingDamageAbility
            {
                Id = "damage-receiver",
                DamageTypes = receiverTypes ?? damageTypes,
                FormulaPolicy = policy ?? FormulaEvaluationPolicy.Strict
            };
            if (mitigation != null)
                receiver.SetMitigationFormula("impact",
                    NumericFormula<CombatFormulaContext>.Compile(mitigation, schema, mitigationFallback));
            Install(Source, ability, trigger);
            Install(Target, receiver);
            Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        }

        public Unit Source { get; }
        public Unit Target { get; }
        public Context Context => context;
        public DamageAbility Ability => ability;

        public void Hit()
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(),
                AbilityId = "delivery",
                Source = Source,
                Target = Target,
                Receiver = Source.EventReceiver
            })).ToList();
            Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
        }

        public void InstallOnTarget(params IHandler[] handlers)
        {
            InstallOn(Target, handlers);
        }

        public void InstallOn(Unit unit, params IHandler[] handlers)
        {
            Install(unit, handlers);
            AdvanceTo(context.ContextTimeSpan);
        }

        public Unit AddUnit(string name, int team)
        {
            Unit unit = CreateUnit(context, name, 1);
            unit.TeamId = team;
            context.AddEventReceiver(unit.EventReceiver);
            return unit;
        }

        public DamageProfile Profile(int level)
        {
            ability.Level = level;
            return DamageProfile.From(ability);
        }

        public PeriodicDamageRequestEvent ScheduleDamageWithoutActor(
            int level, int count, TimeSpan interval, AbilityTriggerChain? chain = null,
            TimeSpan delay = default, CancellationTokenSource? cancellation = null,
            TimeSpan? expiresAt = null, PeriodicEffectHandle? replacement = null)
        {
            DamageProfile profile = Profile(level);
            var request = context.Factory.NewEvent<PeriodicDamageRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = null, Target = Target,
                TickCount = count, TickInterval = interval, Damage = profile,
                Causality = chain, Receiver = Target.EventReceiver, FirstTickDelay = delay,
                CancellationTokenSource = cancellation, ExpiresAt = expiresAt, Replaces = replacement
            };
            context.Dispatch(request).ToList();
            return request;
        }

        public PeriodicEffectStartedEvent Started(PeriodicDamageRequestEvent request) =>
            Target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>()
                .Single(ev => ev.CommandEventId == request.CommandEventId);

        public CommandGateManager ConfigurePeriodicCommand(TimeSpan? cooldown = null, AbilityChargeConfiguration? charges = null)
        {
            var gate = new CommandGateManager
            {
                Cooldown = cooldown ?? TimeSpan.FromSeconds(5), ResourceCost = 10, OccupiesWindup = false,
                Charges = charges
            };
            ability.GateManager = gate;
            if (charges != null) gate.EnterContext(context, ability, Source);
            ability.Tick = new DamageTick { TickCount = 2, TickInterval = TimeSpan.FromSeconds(1) };
            ability.Delay = TimeSpan.FromSeconds(1);
            Install(Source, new TriggerHandler
            {
                Id = "command-trigger", TargetAbilityId = ability.Id, TriggerType = TriggerType.Command
            });
            AdvanceTo(context.ContextTimeSpan);
            return gate;
        }

        public void AddCommandTrigger()
        {
            Install(Source, new TriggerHandler
            {
                Id = "command-trigger-2", TargetAbilityId = ability.Id, TriggerType = TriggerType.Command
            });
            AdvanceTo(context.ContextTimeSpan);
        }

        public CommandEvent ObserveCommandWithDamage() => ObserveCommand(ability);

        public CommandEvent ObserveCommandWithProjectile(ProjectileAbility projectile) => ObserveCommand(projectile);

        private CommandEvent ObserveCommand(AbilityBase response)
        {
            Install(Source, new TestCommandAbility { Id = "observed-command" }, new TriggerHandler
            {
                Id = $"{response.Id}.command-observer", TargetAbilityId = response.Id,
                TriggerType = TriggerType.Command, SourceAbilityIds = ["observed-command"]
            });
            AdvanceTo(context.ContextTimeSpan);
            var command = context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "observed-command", Source = Source, Target = Target, Receiver = Source.EventReceiver
            };
            context.Dispatch(command).ToList();
            AdvanceTo(context.ContextTimeSpan);
            return command;
        }

        public CommandGateManager ConfigureTriggeredDamageGate(
            AbilityChargeConfiguration charges, bool periodic = false, TimeSpan? windup = null)
        {
            var gate = new CommandGateManager
            { Charges = charges, ResourceCost = 10, OccupiesWindup = false, Windup = windup };
            ability.GateManager = gate;
            ability.Windup = windup;
            if (periodic)
            {
                ability.Tick = new DamageTick { TickCount = 2, TickInterval = TimeSpan.FromSeconds(1) };
                ability.Delay = TimeSpan.FromSeconds(1);
            }
            gate.EnterContext(context, ability, Source);
            return gate;
        }

        public void SetDamageLevel(int level) => ability.Level = level;

        public ProjectileAbility ConfigureTriggeredProjectileGate(TimeSpan? windup = null, bool gated = true)
        {
            var gate = new CommandGateManager
            {
                Charges = new AbilityChargeConfiguration(1, 1), ResourceCost = 10,
                OccupiesWindup = false, Windup = windup
            };
            var projectile = new ProjectileAbility
            {
                Id = "follow-up", GateManager = gated ? gate : null, Windup = windup, ProjectileSpeed = 0
            };
            Install(Source, projectile, new TriggerHandler
            {
                Id = "follow-up-trigger", TargetAbilityId = projectile.Id,
                TriggerType = TriggerType.ProjectileHit, SourceAbilityIds = ["delivery"]
            });
            AdvanceTo(context.ContextTimeSpan);
            return projectile;
        }

        public CommandEvent CastPeriodicCommand()
        {
            var command = context.Factory.NewEvent<CommandEvent>() with
            { AbilityId = ability.Id, Source = Source, Target = Target, Receiver = Source.EventReceiver };
            context.Dispatch(command).ToList();
            return command;
        }

        public CommandExecutionEvent CommandResult(CommandEvent command) =>
            Source.EventReceiver.History.OfType<CommandExecutionEvent>()
                .Single(result => ReferenceEquals(result.Command, command));

        public void AdvanceTo(TimeSpan time) =>
            Assert.IsFalse(context.RunUntil(time).Terminated);

        public DamageLifeReduceEvent[] LifeReductionEvents() =>
            context.History.OfType<DamageLifeReduceEvent>()
                .Where(ev => ev.Receiver == Target.EventReceiver).ToArray();

        public int[] LifeReductions() => LifeReductionEvents().Select(ev => ev.LifePointReduce).ToArray();

        public DamageEvent[] TargetDamageEvents() =>
            context.History.OfType<DamageEvent>().Where(ev => ev.Receiver == Target.EventReceiver).ToArray();

        public void Dispose() => context.Dispose();

        private void Install(Unit unit, params IHandler[] handlers)
        {
            TestHandlerFactory.Register(unit, handlers);
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(),
                Source = unit,
                Target = unit,
                AbilityIds = handlers.Select(handler => handler.Id).ToArray(),
                Receiver = unit.EventReceiver
            }).ToList();
        }

        private static Unit CreateUnit(Context context, string id, int level, IReadOnlySet<string>? tags = null)
        {
            var unit = new Unit
            {
                Name = id,
                ArchetypeId = id,
                Tags = tags ?? new HashSet<string>(StringComparer.Ordinal),
                Property = new UnitProperty { LifePoint = 200, LifePointMax = 200, Level = level },
                Abilities = new UnitAbilityManager(),
                EventReceiver = context.Factory.NewEventReceiver()
            };
            unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler());
            unit.Initialize();
            return unit;
        }
    }
}
