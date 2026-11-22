using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using System.Diagnostics;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class ProjectileTriggerRoutingTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void PeriodicHealTicksPublishOnceWithoutDuplicatingSettlementOrChangingTheActor()
    {
        using var battle = new Battle();
        battle.Target.Property.LifePoint = 50;
        foreach (Unit holder in new[] { battle.Source, battle.Target, battle.Ally })
            battle.Install(holder, TriggerSourceScope.Any, type: TriggerType.ScheduledHealTick,
                conditions: [new CallbackCondition(c => c.EventSource == null &&
                    c.EventTarget == battle.Target && c.EventAbility?.DefinitionId == "delivery")]);
        battle.Install(battle.Other, TriggerSourceScope.Self, type: TriggerType.ScheduledHealTick);
        Guid command = Guid.NewGuid();
        ScheduledHealTickEvent? first = null;
        for (int i = 0; i < 2; i++)
        {
            var tick = battle.Context.Factory.NewEvent<ScheduledHealTickEvent>() with
            {
                CommandEventId = command, Source = null, Target = battle.Target,
                Heal = HealProfile.From(new HealAbility { Id = "delivery", BaseHeal = 5 }),
                Receiver = battle.Target.EventReceiver
            };
            first ??= tick;
            battle.Context.Dispatch(tick).ToList();
        }
        battle.Run();
        Assert.AreEqual(60, battle.Target.Property.LifePoint);
        CollectionAssert.AreEqual(new[] { 5, 5 }, battle.Context.History.OfType<HealLifeGainEvent>()
            .Select(ev => ev.LifeGain).ToArray());
        foreach (Unit holder in new[] { battle.Source, battle.Target, battle.Ally })
        {
            Assert.HasCount(2, battle.Recorder(holder).Invocations);
            Assert.IsTrue(battle.Recorder(holder).Invocations.All(ev =>
                ev.Source == holder && ev.Target == battle.Target && ev.CommandEventId == command));
        }
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        Assert.IsTrue(battle.Context.History.OfType<HealPublicationEvent>().All(ev => ev.Heal.Source == null));
        battle.Context.Dispatch(first! with { }).ToList();
        var error = Assert.ThrowsExactly<InvalidOperationException>(battle.Run);
        StringAssert.Contains(error.Message, "already been published");
        Assert.AreEqual(60, battle.Target.Property.LifePoint);
        TestContext.WriteLine("Two actorless ticks healed five each and independently activated each owner; a copied tick could not settle again.");
    }

    [TestMethod]
    public void SignalsPublishOncePerRealEventAndPreserveResponseOwnership()
    {
        using var battle = new Battle();
        var map = new MapRuntime();
        foreach (Unit unit in new[] { battle.Source, battle.Target, battle.Ally })
        {
            Place(map, unit, new Vector2D(0, 0));
            battle.Install(unit, TriggerSourceScope.Any, type: TriggerType.Signal,
                conditions: [new CallbackCondition(c => c.EventSource == battle.Source &&
                    c.EventTarget == battle.Target && c.EventAbility?.DefinitionId == "delivery")]);
        }
        battle.Install(battle.Other, TriggerSourceScope.Any, type: TriggerType.Signal, signalId: "other");
        Guid command = Guid.NewGuid();
        AbilitySignalEvent first = battle.CreateSignal(command);
        battle.Context.Dispatch(first).ToList();
        battle.Context.Dispatch(battle.CreateSignal(command)).ToList();
        battle.Run();

        foreach (Unit unit in new[] { battle.Source, battle.Target, battle.Ally })
        {
            Assert.HasCount(2, battle.Recorder(unit).Invocations);
            Assert.IsTrue(battle.Recorder(unit).Invocations.All(response =>
                response.Source == unit && response.SourceObject == unit.MapObject &&
                response.Target == battle.Target && response.CompletionReceiver == battle.Source.EventReceiver &&
                response.CommandEventId == command));
        }
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        battle.Context.Dispatch(first with { }).ToList();
        var error = Assert.ThrowsExactly<InvalidOperationException>(battle.Run);
        StringAssert.Contains(error.Message, "already been published");
        TestContext.WriteLine("Two real signals sharing a command each reached eligible owners once; a copied publication was rejected.");
    }

    [TestMethod]
    public void ActorlessSignalsRespectSourceScopeConditionsAndProducerFiltersBeforeProbability()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        battle.Install(battle.Source, TriggerSourceScope.Self, probability: 0.5,
            type: TriggerType.Signal, sourceIds: [],
            conditions: [new CallbackCondition(_ => throw new InvalidOperationException("scope must reject first"))]);
        battle.Install(battle.Target, TriggerSourceScope.Any, probability: 0.5,
            type: TriggerType.Signal, sourceIds: [],
            conditions: [new TargetRelationTriggerCondition(TriggerSourceScope.Self),
                new CallbackCondition(c => c.EventSource == null && c.EventAbility == null)]);
        battle.Install(battle.Ally, TriggerSourceScope.Any, probability: 0.5,
            type: TriggerType.Signal, sourceIds: []);
        battle.Install(battle.Other, TriggerSourceScope.Any, probability: 0.5, type: TriggerType.Signal);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AbilitySignalEvent signal = battle.CreateSignal(Guid.NewGuid()) with
        {
            Source = null, AbilitySnapshot = null, Receiver = battle.Target.EventReceiver
        };
        battle.Context.Dispatch(signal with { CancellationTokenSource = cancellation }).ToList();
        battle.Run();
        Assert.AreEqual(0, random.Draws);
        battle.Context.Dispatch(signal).ToList();
        battle.Run();

        Assert.AreEqual(2, random.Draws);
        Assert.HasCount(1, battle.Recorder(battle.Target).Invocations);
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Source).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        Assert.IsNull(((AbilitySignalEvent)battle.Recorder(battle.Ally).Invocations.Single().UpstreamEvent).Source);
        TestContext.WriteLine("Cancelled signals consumed no draws; actorless Any listeners matched, while Self and producer-filtered listeners did not.");
    }

    [TestMethod]
    public void ReinstallingSignalListenerDoesNotReviveItsDispatchedObservation()
    {
        using var battle = new Battle();
        TriggerHandler trigger = battle.Install(battle.Ally, TriggerSourceScope.Any,
            priority: 10, type: TriggerType.Signal);
        battle.Context.Dispatch(battle.CreateSignal(Guid.NewGuid()) with { Priority = -10 }).ToList();
        battle.QueueRemoval(battle.Ally, trigger.Id);
        battle.QueueInstall(battle.Ally, trigger);
        battle.Run();
        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);

        battle.Context.Dispatch(battle.CreateSignal(Guid.NewGuid())).ToList();
        battle.Run();
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("Reinstallation rejected the old dispatched signal binding but received the next real signal.");
    }

    [TestMethod]
    public void SignalsRejectTargetsFromAnotherContext()
    {
        using var battle = new Battle();
        using var foreign = new Battle();
        battle.Install(battle.Ally, TriggerSourceScope.Any, type: TriggerType.Signal);
        battle.Context.Dispatch(battle.CreateSignal(Guid.NewGuid()) with { Target = foreign.Target }).ToList();
        var error = Assert.ThrowsExactly<InvalidOperationException>(battle.Run);
        StringAssert.Contains(error.Message, "does not belong to this context");
        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("A cross-context signal was rejected before any listener invocation.");
    }

    [TestMethod]
    public void OneHitReachesEligibleListenersAcrossReceiversOnlyOnce()
    {
        using var battle = new Battle();
        battle.Install(battle.Source, TriggerSourceScope.Self);
        battle.Install(battle.Target, TriggerSourceScope.Any);
        battle.Install(battle.Ally, TriggerSourceScope.Any);
        battle.Install(battle.Other, TriggerSourceScope.Self);

        battle.Hit(Guid.NewGuid());

        Assert.HasCount(1, battle.Recorder(battle.Source).Invocations);
        Assert.HasCount(1, battle.Recorder(battle.Target).Invocations);
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        Assert.AreSame(battle.Source, battle.Recorder(battle.Source).Invocations.Single().Source);
        Assert.AreSame(battle.Target, battle.Recorder(battle.Target).Invocations.Single().Source);
        Assert.AreSame(battle.Ally, battle.Recorder(battle.Ally).Invocations.Single().Source);
        ProjectileHitPublicationEvent publication = battle.Context.History
            .OfType<ProjectileHitPublicationEvent>().Single();
        ProjectileHitEvent[] deliveries = battle.Context.History.OfType<ProjectileHitEvent>().ToArray();
        Assert.HasCount(2, deliveries);
        Assert.HasCount(1, deliveries.Where(hit => hit.Receiver == battle.Source.EventReceiver));
        Assert.HasCount(1, deliveries.Where(hit => hit.Receiver == battle.Target.EventReceiver));
        Assert.AreSame(publication.Hit, battle.Recorder(battle.Ally).Invocations.Single().UpstreamEvent);
        TestContext.WriteLine("One hit with source and target deliveries activated each eligible listener once; response sources were their owners.");
    }

    [TestMethod]
    public void DistinctHitsFromOneCommandAreNotCollapsed()
    {
        using var battle = new Battle();
        battle.Install(battle.Ally, TriggerSourceScope.Any);
        Guid command = Guid.NewGuid();

        battle.Hit(command);
        battle.Hit(command);

        Assert.HasCount(2, battle.Recorder(battle.Ally).Invocations);
        Assert.IsTrue(battle.Recorder(battle.Ally).Invocations.All(ev => ev.CommandEventId == command));
        TestContext.WriteLine("Two real hits sharing one command each activated the listener.");
    }

    [TestMethod]
    public void ExplosionPublishesOnceToMatchingListenersAcrossUnits()
    {
        using var battle = new Battle();
        battle.Install(battle.Source, TriggerSourceScope.Self, type: TriggerType.ProjectileExplosion);
        battle.Install(battle.Ally, TriggerSourceScope.Any, type: TriggerType.ProjectileExplosion);
        battle.Install(battle.Target, TriggerSourceScope.Enemy, type: TriggerType.ProjectileExplosion);
        battle.Install(battle.Other, TriggerSourceScope.Self, type: TriggerType.ProjectileExplosion);
        Guid command = Guid.NewGuid();

        battle.Explosion(command);
        battle.Explosion(command);

        Assert.HasCount(2, battle.Recorder(battle.Source).Invocations);
        Assert.HasCount(2, battle.Recorder(battle.Ally).Invocations);
        Assert.HasCount(2, battle.Recorder(battle.Target).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        Assert.IsTrue(battle.Recorder(battle.Ally).Invocations.All(ev =>
            ev.CommandEventId == command && ReferenceEquals(ev.Source, battle.Ally) &&
            ev.UpstreamEvent is ProjectileExplosionEvent));
        TestContext.WriteLine("Two explosions sharing a command each reached their eligible listeners once.");
    }

    [TestMethod]
    public void HitAndExplosionSubscriptionsDoNotObserveTheOtherEvent()
    {
        using var battle = new Battle();
        battle.Install(battle.Source, TriggerSourceScope.Self);
        battle.Install(battle.Ally, TriggerSourceScope.Any, type: TriggerType.ProjectileExplosion);

        battle.Hit(Guid.NewGuid());
        Assert.HasCount(1, battle.Recorder(battle.Source).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);
        battle.Explosion(Guid.NewGuid());
        Assert.HasCount(1, battle.Recorder(battle.Source).Invocations);
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("Projectile hits and explosions remained distinct semantic event types.");
    }

    [TestMethod]
    public void OneHealRequestReachesEachListenerOnceWithoutConfusingRequestedAndActualAmounts()
    {
        using var battle = new Battle();
        battle.Target.Property.LifePoint = 95;
        var map = new MapRuntime();
        Place(map, battle.Source, new Vector2D(0, 0));
        Place(map, battle.Target, new Vector2D(1, 0));
        Place(map, battle.Ally, new Vector2D(2, 0));
        foreach (Unit unit in new[] { battle.Source, battle.Target, battle.Ally })
            battle.Install(unit, TriggerSourceScope.Any, type: TriggerType.Heal,
                conditions: [new CallbackCondition(context =>
                    context.EventSource == battle.Source && context.EventTarget == battle.Target &&
                    context.EventAbility?.DefinitionId == "delivery")]);
        HealEvent heal = battle.CreateHeal(battle.Source, battle.Target);
        battle.Context.Dispatch(HealPublicationEvent.FromHeal(heal)).ToList();
        battle.Run();

        Assert.AreEqual(100, battle.Target.Property.LifePoint);
        Assert.HasCount(1, battle.Context.History.OfType<HealPublicationEvent>());
        Assert.HasCount(2, battle.Context.History.OfType<HealEvent>());
        Assert.AreEqual(5, battle.Target.EventReceiver.History.OfType<HealLifeGainEvent>().Single().LifeGain);
        foreach (Unit unit in new[] { battle.Source, battle.Target, battle.Ally })
        {
            AbilityTriggerInvokeEvent response = battle.Recorder(unit).Invocations.Single();
            Assert.AreSame(unit, response.Source);
            Assert.AreSame(unit.MapObject, response.SourceObject);
            Assert.AreSame(heal, response.UpstreamEvent);
            Assert.AreEqual(10, ((HealEvent)response.UpstreamEvent!).HealAmount);
        }
        TestContext.WriteLine("One heal request of 10 produced 5 actual life; source, target and third-party listeners each responded once with their own object.");
    }

    [TestMethod]
    public void SelfHealingHasOneDeliveryAndRepeatedRealRequestsRemainDistinct()
    {
        using var battle = new Battle();
        battle.Source.Property.LifePoint = 50;
        battle.Install(battle.Source, TriggerSourceScope.Self, type: TriggerType.Heal);
        Guid command = Guid.NewGuid();
        for (int i = 0; i < 2; i++)
            battle.Context.Dispatch(HealPublicationEvent.FromHeal(
                battle.CreateHeal(battle.Source, battle.Source) with { CommandEventId = command })).ToList();
        battle.Run();
        Assert.AreEqual(70, battle.Source.Property.LifePoint);
        Assert.HasCount(2, battle.Context.History.OfType<HealEvent>());
        Assert.HasCount(2, battle.Recorder(battle.Source).Invocations);
        TestContext.WriteLine("Two self-heal requests sharing a command each settled once and produced one listener invocation.");
    }

    [TestMethod]
    public void ActorlessHealingMatchesAnyButNotSelfAndKeepsTheActorNull()
    {
        using var battle = new Battle();
        battle.Target.Property.LifePoint = 50;
        battle.Install(battle.Target, TriggerSourceScope.Self, type: TriggerType.Heal, sourceIds: []);
        battle.Install(battle.Ally, TriggerSourceScope.Any, type: TriggerType.Heal,
            conditions: [new CallbackCondition(context => context.EventSource == null && context.EventAbility == null)],
            sourceIds: []);
        battle.Install(battle.Other, TriggerSourceScope.Any, type: TriggerType.Heal);
        HealEvent heal = battle.CreateHeal(null, battle.Target) with
        { HealAbility = HealProfile.ForEffect("environment", 10) };
        battle.Context.Dispatch(HealPublicationEvent.FromHeal(heal)).ToList();
        battle.Run();
        Assert.AreEqual(60, battle.Target.Property.LifePoint);
        Assert.IsEmpty(battle.Recorder(battle.Target).Invocations);
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        Assert.IsNull(battle.Target.EventReceiver.History.OfType<HealLifeGainEvent>().Single().Source);
        TestContext.WriteLine("Actorless healing matched Any but not Self; a producing-ability ID filter did not match a source-less effect.");
    }

    [TestMethod]
    public void CancelingHealBeforeSettlementSkipsProbabilityAndConfirmation()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        using var cancellation = new CancellationTokenSource();
        battle.Target.Property.LifePoint = 50;
        battle.Target.EventReceiver.AddHandler(new CancelHealHandler(cancellation) { Priority = -10 });
        battle.Install(battle.Source, TriggerSourceScope.Self, probability: 0.5, type: TriggerType.Heal);
        battle.Install(battle.Ally, TriggerSourceScope.Any, probability: 0.5, type: TriggerType.Heal);
        battle.Context.Dispatch(HealPublicationEvent.FromHeal(
            battle.CreateHeal(battle.Source, battle.Target) with { CancellationTokenSource = cancellation })).ToList();
        battle.Run();
        Assert.AreEqual(50, battle.Target.Property.LifePoint);
        Assert.AreEqual(0, random.Draws);
        Assert.IsEmpty(battle.Context.History.OfType<HealLifeGainEvent>());
        Assert.IsEmpty(battle.Recorder(battle.Source).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("Target-side cancellation stopped settlement, source/third-party observation and probability; no gain event was fabricated.");
    }

    [TestMethod]
    public void HealPublicationRejectsDuplicateCopiesAndForeignTargets()
    {
        using var battle = new Battle();
        using var foreign = new Battle();
        HealEvent heal = battle.CreateHeal(battle.Source, battle.Target);
        HealPublicationEvent publication = HealPublicationEvent.FromHeal(heal);
        Assert.ThrowsExactly<InvalidOperationException>(() => HealPublicationEvent.FromHeal(heal with { HealAmount = 20 }));
        battle.Context.Dispatch(publication).ToList();
        battle.Run();
        HealEvent delivery = battle.Context.History.OfType<HealEvent>().First();
        Assert.ThrowsExactly<InvalidOperationException>(() => (delivery with { HealAmount = 20 }).Dispatch().ToList());
        Assert.ThrowsExactly<InvalidOperationException>(() => publication.Dispatch().ToList());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            HealPublicationEvent.FromHeal(battle.CreateHeal(battle.Source, foreign.Target)));
        TestContext.WriteLine("Duplicate publication, forged delivery copies and foreign-context healing were explicitly rejected.");
    }

    [TestMethod]
    [DataRow(TriggerSourceScope.Self, false, false)]
    [DataRow(TriggerSourceScope.Ally, true, false)]
    [DataRow(TriggerSourceScope.Enemy, false, true)]
    [DataRow(TriggerSourceScope.Any, true, true)]
    public void EventSourceScopeUsesTheOriginalSource(
        TriggerSourceScope scope, bool allyMatches, bool enemyMatches)
    {
        using var battle = new Battle();
        battle.Install(battle.Ally, scope);
        battle.Install(battle.Target, scope);

        battle.Hit(Guid.NewGuid());

        Assert.HasCount(allyMatches ? 1 : 0, battle.Recorder(battle.Ally).Invocations);
        Assert.HasCount(enemyMatches ? 1 : 0, battle.Recorder(battle.Target).Invocations);
        TestContext.WriteLine($"Scope {scope}: ally={allyMatches}, enemy={enemyMatches}.");
    }

    [TestMethod]
    public void EarlierDeliveryHandlerCanCancelBeforeTriggerProbabilityIsEvaluated()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        using var cancellation = new CancellationTokenSource();
        battle.Source.EventReceiver.AddHandler(new CancelHitHandler(cancellation));
        battle.Install(battle.Source, TriggerSourceScope.Self, probability: 0.5);

        battle.Hit(Guid.NewGuid(), cancellation: cancellation);

        Assert.AreEqual(0, random.Draws);
        Assert.IsEmpty(battle.Recorder(battle.Source).Invocations);
        TestContext.WriteLine("A previously registered hit handler canceled the delivery before the trigger drew probability.");
    }

    [TestMethod]
    public void RejectedSourceScopeSkipsConditionsAndProbability()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        battle.Install(battle.Ally, TriggerSourceScope.Self, probability: 0.5,
            conditions: [new CallbackCondition(_ => throw new InvalidOperationException("must not run"))]);

        battle.Hit(Guid.NewGuid());

        Assert.AreEqual(0, random.Draws);
        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("A mismatched event-source scope skipped the condition and probability.");
    }

    [TestMethod]
    public void UnknownConditionsContinueUntilFalseAndNeverDrawProbability()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        var visited = new List<string>();
        battle.Install(battle.Ally, TriggerSourceScope.Any, probability: 0.5,
            conditions:
            [
                new CallbackCondition(_ => { visited.Add("unknown"); return null; }),
                new CallbackCondition(_ => { visited.Add("false"); return false; }),
                new CallbackCondition(_ => throw new InvalidOperationException("must not run"))
            ]);

        battle.Hit(Guid.NewGuid());

        CollectionAssert.AreEqual(new[] { "unknown", "false" }, visited);
        Assert.AreEqual(0, random.Draws);
        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("An unknown condition evaluated the following false condition, which short-circuited before probability.");
    }

    [TestMethod]
    public void UnknownConditionsDoNotHideLaterEvaluationErrors()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        battle.Install(battle.Ally, TriggerSourceScope.Any, probability: 0.5,
            conditions:
            [
                new CallbackCondition(_ => null),
                new CallbackCondition(_ => throw new InvalidOperationException("condition failed"))
            ]);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => battle.Hit(Guid.NewGuid()));

        StringAssert.Contains(error.Message, "condition failed");
        Assert.AreEqual(0, random.Draws);
        TestContext.WriteLine("An unknown condition did not suppress the next condition's error or draw probability.");
    }

    [TestMethod]
    public void MissingListenerBindingIsNotTreatedAsAnAbsentAbilitySource()
    {
        using var battle = new Battle();
        battle.Install(battle.Ally, TriggerSourceScope.Any,
            conditions: [new CallbackCondition(context => context.ListenerAbility.GrantSourceInstanceId == null)]);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => battle.Hit(Guid.NewGuid()));

        StringAssert.Contains(error.Message, "bound ability instance");
        TestContext.WriteLine("An unbound listener failed explicitly instead of satisfying a null-source condition.");
    }

    [TestMethod]
    public void TargetRelationAndDistanceUseTheOriginalEventAndSameMap()
    {
        using var battle = new Battle();
        var firstMap = new MapRuntime();
        var secondMap = new MapRuntime();
        Place(firstMap, battle.Source, new Vector2D(0, 0));
        Place(firstMap, battle.Target, new Vector2D(10, 0));
        Place(firstMap, battle.Ally, new Vector2D(2, 0));
        Place(secondMap, battle.Other, new Vector2D(10, 0));
        battle.Install(battle.Target, TriggerSourceScope.Any,
            conditions: [new TargetRelationTriggerCondition(TriggerSourceScope.Self)]);
        battle.Install(battle.Ally, TriggerSourceScope.Any,
            conditions:
            [
                new TargetRelationTriggerCondition(TriggerSourceScope.Enemy),
                new DistanceTriggerCondition(TriggerDistanceAnchor.Source, 2),
                new DistanceTriggerCondition(TriggerDistanceAnchor.Target, 8),
                new DistanceTriggerCondition(TriggerDistanceAnchor.HitPoint, 8)
            ]);
        battle.Install(battle.Other, TriggerSourceScope.Any,
            conditions: [new DistanceTriggerCondition(TriggerDistanceAnchor.Target, 100)]);

        ProjectileHitEvent hit = battle.CreateHit(Guid.NewGuid()) with
        {
            SourceObject = battle.Source.MapObject, TargetObject = battle.Target.MapObject,
            HitPoint = battle.Target.MapObject!.Position, HitMap = firstMap
        };
        battle.Context.Dispatch(ProjectileHitPublicationEvent.FromHit(hit)).ToList();
        battle.Run();

        Assert.HasCount(1, battle.Recorder(battle.Target).Invocations);
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        TestContext.WriteLine("Target Self, enemy and all distance anchors matched the original hit; equal coordinates on another map did not.");
    }

    [TestMethod]
    public void ExplosionDistanceUsesTheImpactMapRatherThanTheActorsCurrentMap()
    {
        using var battle = new Battle();
        var impactMap = new MapRuntime();
        var currentMap = new MapRuntime();
        Place(currentMap, battle.Source, new Vector2D(0, 0));
        Place(impactMap, battle.Ally, new Vector2D(0, 0));
        Place(currentMap, battle.Other, new Vector2D(0, 0));
        foreach (Unit holder in new[] { battle.Ally, battle.Other })
            battle.Install(holder, TriggerSourceScope.Any, type: TriggerType.ProjectileExplosion,
                conditions: [new DistanceTriggerCondition(TriggerDistanceAnchor.HitPoint, 1)]);
        battle.Context.Dispatch(battle.Context.Factory.NewEvent<ProjectileExplosionEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = battle.Source,
            SourceObject = battle.Source.MapObject, HitPoint = new Vector2D(0, 0), HitMap = impactMap,
            Receiver = battle.Source.EventReceiver
        }).ToList();
        battle.Run();
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        Assert.IsEmpty(battle.Recorder(battle.Other).Invocations);
        TestContext.WriteLine("An explosion retained its impact map even though the actor occupied equal coordinates on another map.");
    }

    [TestMethod]
    public void EarlierDeliveryHandlerChangesTheLiveRelationBeforeTheTrigger()
    {
        using var battle = new Battle();
        battle.Target.EventReceiver.AddHandler(new ChangeTeamOnHitHandler(battle.Source, battle.Target.TeamId));
        battle.Install(battle.Target, TriggerSourceScope.Enemy);

        battle.Hit(Guid.NewGuid());

        Assert.AreEqual(battle.Target.TeamId, battle.Source.TeamId);
        Assert.IsEmpty(battle.Recorder(battle.Target).Invocations);
        TestContext.WriteLine("A same-priority earlier delivery handler changed the team before the trigger evaluated its scope.");
    }

    [TestMethod]
    [DataRow(true, 0, 0, true)]
    [DataRow(true, 0, -10, false)]
    [DataRow(false, -10, 0, true)]
    [DataRow(false, 10, 0, false)]
    public void DeliveryHandlersAndTriggersKeepPriorityAndRegistrationOrder(
        bool triggerFirst, int triggerPriority, int handlerPriority, bool expectedMatch)
    {
        using var battle = new Battle();
        var changeTeam = new ChangeTeamOnHitHandler(battle.Source, battle.Target.TeamId) { Priority = handlerPriority };
        if (!triggerFirst) battle.Target.EventReceiver.AddHandler(changeTeam);
        battle.Install(battle.Target, TriggerSourceScope.Enemy, triggerPriority);
        if (triggerFirst) battle.Target.EventReceiver.AddHandler(changeTeam);

        battle.Hit(Guid.NewGuid());

        Assert.HasCount(expectedMatch ? 1 : 0, battle.Recorder(battle.Target).Invocations);
        TestContext.WriteLine(
            $"Trigger first={triggerFirst}, priorities={triggerPriority}/{handlerPriority}, matched={expectedMatch}.");
    }

    [TestMethod]
    public void ReinstallingTheSameTriggerDoesNotReviveAQueuedOldObservation()
    {
        using var battle = new Battle();
        TriggerHandler trigger = battle.Install(battle.Ally, TriggerSourceScope.Any, priority: 10);
        battle.QueueHit(Guid.NewGuid(), priority: -10);
        battle.QueueRemoval(battle.Ally, trigger.Id);
        battle.QueueInstall(battle.Ally, trigger);

        battle.Run();

        Assert.IsEmpty(battle.Recorder(battle.Ally).Invocations);
        battle.Hit(Guid.NewGuid());
        Assert.HasCount(1, battle.Recorder(battle.Ally).Invocations);
        TestContext.WriteLine("Reinstalling the same object did not reactivate its old queued observation; a later hit was observed.");
    }

    [TestMethod]
    public void InstalledTriggerConfigurationCannotBeMutated()
    {
        using var battle = new Battle();
        TriggerHandler trigger = battle.Install(battle.Ally, TriggerSourceScope.Any);

        Assert.ThrowsExactly<InvalidOperationException>(() => trigger.Config.EventSourceScope = TriggerSourceScope.Self);
        Assert.ThrowsExactly<InvalidOperationException>(() => trigger.TargetAbilityId = "other");
        Assert.ThrowsExactly<InvalidOperationException>(() => trigger.Priority = 7);
        Assert.ThrowsExactly<InvalidOperationException>(() => trigger.Id = "other-trigger");
        Assert.ThrowsExactly<InvalidOperationException>(() => trigger.StageBindTarget(new AtomicTransaction(), Guid.NewGuid()));
        TestContext.WriteLine("A registered trigger's scope, target, and priority are immutable.");
    }

    [TestMethod]
    public void InstallationBeforePublicationObservesTheAlreadyQueuedHit()
    {
        using var battle = new Battle();
        var recorder = new RecorderHandler { Id = "late-reaction" };
        var trigger = NewTrigger("late-trigger", recorder.Id, "delivery");
        battle.QueueHit(Guid.NewGuid(), priority: 10);
        battle.QueueInstall(battle.Ally, recorder, trigger);

        battle.Run();

        Assert.HasCount(1, recorder.Invocations);
        TestContext.WriteLine("A listener installed before actual publication observed the hit despite later queue insertion.");
    }

    [TestMethod]
    public void InstallationAfterPublicationDoesNotReplayTheHit()
    {
        using var battle = new Battle();
        var recorder = new RecorderHandler { Id = "late-reaction" };
        var trigger = NewTrigger("late-trigger", recorder.Id, "delivery");
        battle.QueueHit(Guid.NewGuid(), priority: -10);
        battle.QueueInstall(battle.Ally, recorder, trigger);

        battle.Run();

        Assert.IsEmpty(recorder.Invocations);
        battle.Hit(Guid.NewGuid());
        Assert.HasCount(1, recorder.Invocations);
        TestContext.WriteLine("A listener installed after publication did not receive the old hit, but observed a later hit.");
    }

    [TestMethod]
    public void AHitCannotBePublishedTwiceOrAcrossContexts()
    {
        using var first = new Battle();
        using var second = new Battle();
        ProjectileHitEvent hit = first.CreateHit(Guid.NewGuid());
        ProjectileHitPublicationEvent.FromHit(hit);

        Assert.ThrowsExactly<InvalidOperationException>(() => ProjectileHitPublicationEvent.FromHit(hit));
        Assert.ThrowsExactly<InvalidOperationException>(() => ProjectileHitPublicationEvent.FromHit(hit with { AbilityId = "other" }));
        ProjectileHitEvent foreignTarget = first.CreateHit(Guid.NewGuid()) with { Target = second.Target };
        Assert.ThrowsExactly<InvalidOperationException>(() => ProjectileHitPublicationEvent.FromHit(foreignTarget));
        TestContext.WriteLine("Repeated publication and a target from another context were explicitly rejected.");
    }

    [TestMethod]
    public void ListenersAreIsolatedByContext()
    {
        using var first = new Battle();
        using var second = new Battle();
        first.Install(first.Ally, TriggerSourceScope.Any);
        second.Install(second.Ally, TriggerSourceScope.Any);

        first.Hit(Guid.NewGuid());
        Assert.HasCount(1, first.Recorder(first.Ally).Invocations);
        Assert.IsEmpty(second.Recorder(second.Ally).Invocations);
        second.Hit(Guid.NewGuid());
        Assert.HasCount(1, first.Recorder(first.Ally).Invocations);
        Assert.HasCount(1, second.Recorder(second.Ally).Invocations);
        TestContext.WriteLine("Two contexts retained separate subscriptions and observations.");
    }

    [TestMethod]
    public void UnfilteredAndIndexedListenersBothObserveOneHit()
    {
        using var battle = new Battle();
        var recorder = new RecorderHandler { Id = "mixed-reaction" };
        var unfiltered = new TriggerHandler
        {
            Id = "unfiltered",
            TargetAbilityId = recorder.Id,
            Config = new AbilityTriggerConfig
            {
                Type = TriggerType.ProjectileHit,
                EventSourceScope = TriggerSourceScope.Any
            }
        };
        battle.QueueInstall(battle.Ally, recorder, unfiltered, NewTrigger("indexed", recorder.Id, "delivery"));
        battle.Run();

        battle.Hit(Guid.NewGuid());

        Assert.HasCount(2, recorder.Invocations);
        TestContext.WriteLine("An unfiltered fallback and an ID-indexed listener each observed the same hit once.");
    }

    [TestMethod]
    public void DeliveryCannotBeDispatchedAgainOrCloned()
    {
        using var battle = new Battle();
        battle.Install(battle.Source, TriggerSourceScope.Self);
        battle.Hit(Guid.NewGuid());
        ProjectileHitEvent delivery = battle.Context.History.OfType<ProjectileHitEvent>()
            .Single(hit => hit.Receiver == battle.Source.EventReceiver);

        Assert.ThrowsExactly<InvalidOperationException>(() => delivery.Dispatch().ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => (delivery with { AbilityId = "other" }).Dispatch().ToArray());
        Assert.HasCount(1, battle.Recorder(battle.Source).Invocations);
        TestContext.WriteLine("Neither the original delivery nor a modified record copy can be dispatched again.");
    }

    [TestMethod]
    public void IndexPlansOnlyMatchingListenersInARepresentativeLoad()
    {
        using var battle = new Battle();
        var observer = new InvocationCounter();
        battle.Context.AddObserver(observer);
        var recorder = new RecorderHandler { Id = "bulk-reaction" };
        IHandler[] handlers = Enumerable.Range(0, 1002)
            .Select(i => (IHandler)NewTrigger(
                $"bulk-trigger-{i}", recorder.Id, i < 1000 ? $"other-{i}" : "delivery"))
            .Prepend(recorder)
            .ToArray();
        battle.QueueInstall(battle.Source, handlers);
        battle.Run();

        battle.Hit(Guid.NewGuid());
        Assert.AreEqual(2, observer.ProjectileInvocations);
        Assert.HasCount(2, recorder.Invocations);

        const int measuredHits = 25;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < measuredHits; i++)
            battle.Hit(Guid.NewGuid());
        timer.Stop();
        Assert.AreEqual(2 * (measuredHits + 1), observer.ProjectileInvocations);
        Assert.HasCount(2 * (measuredHits + 1), recorder.Invocations);
        TestContext.WriteLine(
            $"1000 nonmatching and 2 matching listeners: 2 plans per hit; {measuredHits} additional hits took {timer.Elapsed.TotalMilliseconds:F2} ms.");
    }

    private static TriggerHandler NewTrigger(string id, string target, string source) => new()
    {
        Id = id,
        TargetAbilityId = target,
        Config = new AbilityTriggerConfig
        {
            Type = TriggerType.ProjectileHit,
            SourceAbilityIds = [source],
            EventSourceScope = TriggerSourceScope.Any
        }
    };

    private sealed class InvocationCounter : IContextObserver
    {
        public int ProjectileInvocations { get; private set; }
        public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation)
        {
            if (handlerInvocation && (ev is ProjectileHitTriggerObservationEvent ||
                ev.GetType() == typeof(ProjectileHitEvent)))
                ProjectileInvocations++;
            return [];
        }
    }

    private sealed class CountingRandom : Random
    {
        public int Draws { get; private set; }
        public override double NextDouble()
        {
            Draws++;
            return 0.25;
        }
    }

    private sealed class CallbackCondition(Func<TriggerConditionContext, bool?> evaluate) : ITriggerCondition
    {
        public bool? Evaluate(TriggerConditionContext context) => evaluate(context);
    }

    private static void Place(MapRuntime map, Unit unit, Vector2D position)
    {
        var body = new MapObject(unit.Id.ToString("N"), position, unit.EventReceiver);
        map.AddObject(body);
        unit.MapObjects.Initialize(body);
    }

    private sealed class CancelHitHandler(CancellationTokenSource cancellation) :
        Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnHit(ProjectileHitEvent ev)
        {
            cancellation.Cancel();
            yield break;
        }

    }

    private sealed class CancelHealHandler(CancellationTokenSource cancellation) :
        Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnHeal(HealEvent ev)
        {
            cancellation.Cancel();
            yield break;
        }
    }

    private sealed class ChangeTeamOnHitHandler(Unit source, int team) :
        Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnHit(ProjectileHitEvent ev)
        {
            source.TeamId = team;
            yield break;
        }
    }

    private sealed class RecorderHandler : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        public List<AbilityTriggerInvokeEvent> Invocations { get; } = [];

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId == Id) Invocations.Add(ev);
            yield break;
        }
    }

    private sealed class Battle : IDisposable
    {
        private readonly Dictionary<Unit, RecorderHandler> recorders = [];

        public Battle(Random? random = null)
        {
            Context = new Context(causalityFactory: new TriggerChainFactory(), random: random);
            Context.Logger.Output = null;
            Source = CreateUnit("source", 1);
            Target = CreateUnit("target", 2);
            Ally = CreateUnit("ally", 1);
            Other = CreateUnit("other", 2);
        }

        public Context Context { get; }
        public Unit Source { get; }
        public Unit Target { get; }
        public Unit Ally { get; }
        public Unit Other { get; }

        public TriggerHandler Install(Unit owner, TriggerSourceScope scope, int priority = 0,
            double? probability = null, TriggerType type = TriggerType.ProjectileHit,
            IReadOnlyList<ITriggerCondition>? conditions = null, IReadOnlyList<string>? sourceIds = null,
            string signalId = "pulse")
        {
            var recorder = new RecorderHandler { Id = $"reaction-{owner.Name}" };
            var trigger = new TriggerHandler
            {
                Id = $"trigger-{owner.Name}", TargetAbilityId = recorder.Id, Priority = priority,
                Config = new AbilityTriggerConfig
                {
                    Type = type,
                    SignalId = type == TriggerType.Signal ? signalId : null,
                    SourceAbilityIds = sourceIds ?? ["delivery"],
                    EventSourceScope = scope,
                    Probability = probability,
                    Conditions = conditions ?? []
                }
            };
            recorders.Add(owner, recorder);
            TestHandlerFactory.Register(owner, recorder, trigger);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                AbilityIds = [recorder.Id, trigger.Id], Receiver = owner.EventReceiver
            }).ToList();
            Run();
            return trigger;
        }

        public RecorderHandler Recorder(Unit unit) => recorders[unit];

        public AbilitySignalEvent CreateSignal(Guid command) =>
            Context.Factory.NewEvent<AbilitySignalEvent>() with
            {
                SignalId = "pulse", CommandEventId = command, Source = Source, Target = Target,
                AbilitySnapshot = AbilityRuntimeSnapshot.Capture(new HealAbility { Id = "delivery" }),
                Receiver = Source.EventReceiver, CompletionReceiver = Source.EventReceiver
            };

        public ProjectileHitEvent CreateHit(Guid command, CancellationTokenSource? cancellation = null, int priority = 0)
        {
            return Context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = command, AbilityId = "delivery", Source = Source, Target = Target,
                Receiver = Source.EventReceiver, Priority = priority, CancellationTokenSource = cancellation
            };
        }

        public void QueueHit(Guid command, CancellationTokenSource? cancellation = null, int priority = 0)
        {
            Context.Dispatch(ProjectileHitPublicationEvent.FromHit(CreateHit(command, cancellation, priority))).ToList();
        }

        public void Hit(Guid command, CancellationTokenSource? cancellation = null, int priority = 0)
        {
            QueueHit(command, cancellation, priority);
            Run();
        }

        public void Explosion(Guid command)
        {
            Context.Dispatch(Context.Factory.NewEvent<ProjectileExplosionEvent>() with
            {
                CommandEventId = command, AbilityId = "delivery", Source = Source,
                Receiver = Source.EventReceiver
            }).ToList();
            Run();
        }

        public HealEvent CreateHeal(Unit? source, Unit target) =>
            Context.Factory.NewEvent<HealEvent>() with
            {
                CommandEventId = Guid.NewGuid(),
                HealAbility = HealProfile.From(new HealAbility { Id = "delivery", BaseHeal = 10 }),
                Source = source, Target = target, SourceObject = source?.MapObject, TargetObject = target.MapObject,
                HealAmount = 10, Receiver = target.EventReceiver
            };

        public void QueueRemoval(Unit owner, string id)
        {
            Context.Dispatch(Context.Factory.NewEvent<AbilityUninstallEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                AbilityIds = [id], Receiver = owner.EventReceiver
            }).ToList();
        }

        public void QueueInstall(Unit owner, params IHandler[] handlers)
        {
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = owner, Target = owner,
                Handlers = handlers, Receiver = owner.EventReceiver
            }).ToList();
        }

        private Unit CreateUnit(string name, int team)
        {
            var unit = new Unit
            {
                Name = name, ArchetypeId = name, TeamId = team,
                Property = new UnitProperty { LifePoint = 100, LifePointMax = 100 },
                Abilities = new UnitAbilityManager(), EventReceiver = Context.Factory.NewEventReceiver()
            };
            unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler());
            Context.AddEventReceiver(unit.EventReceiver);
            unit.Initialize();
            return unit;
        }

        public void Run() => Assert.IsFalse(Context.RunUntil(TimeSpan.Zero).Terminated);
        public void Dispose() => Context.Dispose();
    }
}
