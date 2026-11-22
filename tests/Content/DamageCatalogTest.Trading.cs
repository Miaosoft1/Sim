using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Content.Tests;

public sealed partial class DamageCatalogTest
{
    [TestMethod]
    public void ConsentedTradeSwapsOriginalItemsAbilitiesLevelsChargesAndCooldownsAtNetCapacity()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, """
            ability:
              - id: endurance
                type: BaseProperty
                allowMultiple: false
                trigger: { type: None }
                data: { modifiers: [{ target: lifePointMax, formula: "ability.level * 10" }] }
              - id: delivery
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
              - id: recovery
                type: Heal
                allowMultiple: false
                trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                data:
                  amount: 7
                  gate: { cooldownSeconds: 5, charges: { maximum: 2, initial: 2 } }
            """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        firstOwner.Property.LifePoint = secondOwner.Property.LifePoint = 50;
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance first = CreateInventoryItem(context, firstOwner, new(
            "first", "First", "weapon", [new("endurance", 1), new("recovery", 2)]));
        ItemInstance second = CreateInventoryItem(context, secondOwner, new(
            "second", "Second", "weapon", [new("endurance", 5), new("recovery", 4)]));
        ChangeInventory(context, firstOwner, first, InventoryOperation.Acquire, "main");
        ChangeInventory(context, secondOwner, second, InventoryOperation.Acquire, "main");
        AbilityBase firstProperty = first.Source.Abilities.OfType<PropertyAbility>().Single();
        AbilityBase secondProperty = second.Source.Abilities.OfType<PropertyAbility>().Single();
        HealAbility firstHeal = first.Source.Abilities.OfType<HealAbility>().Single();
        HealAbility secondHeal = second.Source.Abilities.OfType<HealAbility>().Single();
        CommandGateManager gate = firstHeal.GateManager!;
        void Hit(Unit owner)
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "delivery",
                Source = owner, Target = owner, Receiver = owner.EventReceiver
            })).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }
        Hit(firstOwner);
        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(first.InstanceId, "main")]);
        ChangeTrade(context, secondOwner, session, TradeAction.Offer, [new(second.InstanceId, "main")]);
        Assert.AreEqual(TradeStatus.AwaitingConsent,
            ChangeTrade(context, firstOwner, session, TradeAction.Confirm).Status);
        Assert.AreEqual(firstOwner.Id, first.OwnerId);
        Assert.AreEqual(TradeStatus.Completed,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        Assert.IsTrue(session.IsClosed);
        Assert.AreSame(second, firstInventory.Items.Single().Item);
        Assert.AreSame(first, secondInventory.Items.Single().Item);
        Assert.AreEqual(firstOwner.Id, second.OwnerId);
        Assert.AreEqual(secondOwner.Id, first.OwnerId);
        Assert.AreSame(secondProperty, firstOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreSame(firstProperty, secondOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreSame(secondHeal, firstOwner.EventReceiver.GetHandlers<HealAbility>().Single());
        Assert.AreSame(firstHeal, secondOwner.EventReceiver.GetHandlers<HealAbility>().Single());
        Assert.AreSame(gate, firstHeal.GateManager);
        Assert.AreEqual(2, firstHeal.Level);
        Assert.AreEqual(4, secondHeal.Level);
        Assert.AreEqual(150, firstOwner.Property.LifePointMax);
        Assert.AreEqual(110, secondOwner.Property.LifePointMax);
        Hit(secondOwner);
        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        Assert.HasCount(1, secondOwner.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.CoolingDown));
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Hit(secondOwner);
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(57, secondOwner.Property.LifePoint);
        Assert.AreEqual(TradeFailure.Closed,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Failure);
        TestContext.WriteLine("Two full equipped inventories exchanged X+IA and Y+IB atomically; the original level-2 gate retained its spent charge and cooldown under the new owner, and completed consent could not execute twice.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConsentedOneWayGiftTransfersOnlyItsOriginalInstanceOrRejectsTheWholeLayout(bool blocked)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        if (blocked)
            ChangeInventory(context, secondOwner,
                CreateInventoryItem(context, secondOwner, new("occupied", "Occupied", "weapon", [new("endurance", 5)])),
                InventoryOperation.Acquire, "main");
        AbilityBase original = gift.Source.Abilities.Single();
        long firstRevision = firstInventory.Revision;
        long secondRevision = secondInventory.Revision;
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        TradeResultEvent result = ChangeTrade(context, secondOwner, session, TradeAction.Confirm);
        if (blocked)
        {
            Assert.AreEqual(TradeFailure.InvalidLayout, result.Failure);
            Assert.AreEqual(firstRevision, firstInventory.Revision);
            Assert.AreEqual(secondRevision, secondInventory.Revision);
            Assert.AreSame(gift, firstInventory.Items.Single().Item);
            Assert.AreSame(firstOwner, gift.Source.Holder);
            Assert.AreEqual(110, firstOwner.Property.LifePointMax);
            Assert.AreEqual(150, secondOwner.Property.LifePointMax);
        }
        else
        {
            Assert.AreEqual(TradeStatus.Completed, result.Status);
            Assert.IsEmpty(firstInventory.Items);
            Assert.AreSame(gift, secondInventory.Items.Single().Item);
            Assert.AreSame(original, secondOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
            Assert.AreSame(secondOwner, gift.Source.Holder);
            Assert.AreEqual(100, firstOwner.Property.LifePointMax);
            Assert.AreEqual(110, secondOwner.Property.LifePointMax);
        }
        Assert.AreEqual(blocked ? 2 : 1, firstInventory.CarryCount + secondInventory.CarryCount);
        TestContext.WriteLine($"One-way gift blocked={blocked}: no counterpart item was fabricated; final-layout rejection preserved both original inventories and capabilities.");
    }

    [TestMethod]
    public void InventoryDiscardInvalidatesTradeConsentWithoutLockingTheInventory()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        Assert.IsTrue(ChangeInventory(context, firstOwner, gift, InventoryOperation.Discard).Success);
        Assert.AreEqual(TradeFailure.InventoryChanged,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Failure);
        Assert.IsTrue(gift.IsDestroyed);
        Assert.IsEmpty(firstInventory.Items);
        Assert.IsEmpty(secondInventory.Items);
        Assert.IsNull(gift.Source.Holder);
        Assert.AreEqual(100, firstOwner.Property.LifePointMax);
        TestContext.WriteLine("Offering/consenting did not lock inventory; discard destroyed the item and its version invalidated outstanding consent instead of transferring a stale instance.");
    }

    [TestMethod]
    public void TradeProjectionFailureLeavesConsentOpenAndAllOriginalOwnershipUntilAValidRetry()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 1, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 1, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("fragile", "Fragile", "weapon", [new("fragile", 2)]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire);
        AbilityBase original = gift.Source.Abilities.Single();
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        long firstRevision = firstInventory.Revision;
        long secondRevision = secondInventory.Revision;
        Assert.ThrowsExactly<ExpressionEvaluationException>(() =>
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm));
        Assert.IsFalse(session.IsClosed);
        Assert.AreEqual(firstRevision, firstInventory.Revision);
        Assert.AreEqual(secondRevision, secondInventory.Revision);
        Assert.AreSame(gift, firstInventory.Items.Single().Item);
        Assert.IsEmpty(secondInventory.Items);
        Assert.AreEqual(firstOwner.Id, gift.OwnerId);
        Assert.IsNull(gift.Source.Holder);
        Assert.AreEqual(100, secondOwner.Property.LifePointMax);
        Assert.IsEmpty(secondOwner.EventReceiver.History.OfType<TradeResultEvent>()
            .Where(result => result.Status == TradeStatus.Completed));
        ChangeSources(context, firstOwner, levels: [new(gift.Source, original.RuntimeInstanceId, 1)]);
        Assert.AreEqual(TradeStatus.Completed,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        Assert.AreSame(original, secondOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(110, secondOwner.Property.LifePointMax);
        TestContext.WriteLine("A trade projection failure did not close consent, move an item or publish completion; the same original instance transferred after a valid in-place repair and retry.");
    }

    [TestMethod]
    public void TradeRechecksTheSharedPolicyAtActualCommitRatherThanOnlyAtConfirmation()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        bool allowed = true;
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => allowed);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        Action<IEvent> observer = ev =>
        {
            if (ev is AbilitySourcesChangeRequestEvent) allowed = false;
        };
        context.EventQueued += observer;
        Assert.AreEqual(TradeFailure.PolicyRejected,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Failure);
        context.EventQueued -= observer;
        Assert.IsFalse(session.IsClosed);
        Assert.AreEqual(firstOwner.Id, gift.OwnerId);
        Assert.AreSame(firstOwner, gift.Source.Holder);
        Assert.IsEmpty(secondInventory.Items);
        allowed = true;
        Assert.AreEqual(TradeStatus.Completed,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        Assert.AreSame(gift, secondInventory.Items.Single().Item);
        TestContext.WriteLine("Eligibility changed between confirmation and source execution; the shared policy rejected the whole commit, then allowed a fresh valid confirmation.");
    }

    [TestMethod]
    public void ChangedTradeTermsRevokeBothConsentsBeforeEitherOriginalItemCanMove()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        long oldTerms = session.TermsRevision;
        ChangeTrade(context, secondOwner, session, TradeAction.Offer, []);
        Assert.AreEqual(oldTerms + 1, session.TermsRevision);
        Assert.AreEqual(TradeStatus.AwaitingConsent,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        Assert.AreEqual(firstOwner.Id, gift.OwnerId);
        Assert.AreSame(gift, firstInventory.Items.Single().Item);
        Assert.IsEmpty(secondInventory.Items);
        Assert.AreEqual(TradeStatus.Completed,
            ChangeTrade(context, firstOwner, session, TradeAction.Confirm).Status);
        Assert.AreSame(gift, secondInventory.Items.Single().Item);
        TestContext.WriteLine("A new terms revision revoked old consent; the original gift moved only after both participants confirmed the new terms.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CommittedTradePublisherFailureDoesNotRollBackOriginalItemsOrLoseCompletion(bool failTradeResult)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        AbilityBase original = gift.Source.Abilities.Single();
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        Guid operationId = Guid.NewGuid();
        bool failed = false;
        Action<IEvent> observer = ev =>
        {
            if (!failed && (failTradeResult
                ? ev is TradeResultEvent { Status: TradeStatus.Completed }
                : ev is AbilitySourcesChangedEvent))
            {
                failed = true;
                throw new InvalidOperationException("trade publication failed after commit");
            }
        };
        context.EventQueued += observer;
        context.Dispatch(context.Factory.NewEvent<TradeRequestEvent>() with
        {
            Actor = secondOwner, Session = session, Action = TradeAction.Confirm,
            ExpectedTermsRevision = session.TermsRevision, OperationId = operationId,
            Receiver = secondOwner.EventReceiver
        }).ToList();
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        context.EventQueued -= observer;
        Assert.AreEqual(operationId, error.OperationId);
        Assert.IsTrue(session.IsClosed);
        Assert.IsEmpty(firstInventory.Items);
        Assert.AreSame(gift, secondInventory.Items.Single().Item);
        Assert.AreSame(original, secondOwner.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.AreEqual(100, firstOwner.Property.LifePointMax);
        Assert.AreEqual(110, secondOwner.Property.LifePointMax);
        context.AdvanceTo(TimeSpan.Zero);
        TradeResultEvent completion = secondOwner.EventReceiver.History.OfType<TradeResultEvent>()
            .Single(result => result.Status == TradeStatus.Completed);
        Assert.AreEqual(operationId, completion.CommittedOperationId);
        Assert.AreEqual(TradeFailure.Closed,
            ChangeTrade(context, firstOwner, session, TradeAction.Confirm).Failure);
        TestContext.WriteLine($"Committed trade survived publisher failure (trade result={failTradeResult}); original ownership and projection stayed committed and exactly one completion remained deliverable.");
    }

    [TestMethod]
    public void TradeRejectsAnOldConfirmationIfTermsChangeBeforeItsSourceCommitActuallyExecutes()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        long oldTerms = session.TermsRevision;
        bool changed = false;
        Action<IEvent> observer = ev =>
        {
            if (ev is not AbilitySourcesChangeRequestEvent || changed) return;
            changed = true;
            context.Dispatch(context.Factory.NewEvent<TradeRequestEvent>() with
            {
                Actor = firstOwner, Session = session, Action = TradeAction.Offer,
                Offers = [new(gift.InstanceId, "main")], ExpectedTermsRevision = oldTerms,
                Priority = -1, Receiver = firstOwner.EventReceiver
            }).ToList();
        };
        context.EventQueued += observer;
        Assert.AreEqual(TradeFailure.StaleTerms,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Failure);
        context.EventQueued -= observer;
        Assert.AreEqual(oldTerms + 1, session.TermsRevision);
        Assert.IsFalse(session.IsClosed);
        Assert.AreSame(gift, firstInventory.Items.Single().Item);
        Assert.IsEmpty(secondInventory.Items);
        Assert.AreEqual(TradeStatus.AwaitingConsent,
            ChangeTrade(context, firstOwner, session, TradeAction.Confirm).Status);
        Assert.AreEqual(TradeStatus.Completed,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        TestContext.WriteLine("A real queued offer preempted the old final source commit; it rejected stale terms and moved the original item only after two new confirmations.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TradeCommitCannotMutateAnInventoryAfterEitherRequiredFeatureWasDetached(bool removeTrading)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadSourceCatalog(provider, InventoryAbilities);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit firstOwner = CreateSourceUnit(context, catalog, "first");
        Unit secondOwner = CreateSourceUnit(context, catalog, "second");
        UnitInventory firstInventory = AttachInventory(firstOwner, 0, 1);
        UnitInventory secondInventory = AttachInventory(secondOwner, 0, 1);
        firstOwner.EventReceiver.AddHandler(new TradeHandler(firstInventory));
        secondOwner.EventReceiver.AddHandler(new TradeHandler(secondInventory));
        ItemInstance gift = CreateInventoryItem(context, firstOwner, new("gift", "Gift", "weapon", [new("endurance")]));
        ChangeInventory(context, firstOwner, gift, InventoryOperation.Acquire, "main");
        var session = new TradeSession(firstInventory, secondInventory, (_, _) => true);
        ChangeTrade(context, firstOwner, session, TradeAction.Offer, [new(gift.InstanceId, "main")]);
        ChangeTrade(context, firstOwner, session, TradeAction.Confirm);
        IHandler feature = removeTrading
            ? firstOwner.EventReceiver.GetHandlers<TradeHandler>().Single()
            : firstOwner.EventReceiver.GetHandlers<InventoryHandler>().Single();
        long firstRevision = firstInventory.Revision;
        long secondRevision = secondInventory.Revision;
        Action<IEvent> observer = ev =>
        {
            if (ev is AbilitySourcesChangeRequestEvent)
                Assert.IsTrue(firstOwner.EventReceiver.TryRemoveHandler(feature));
        };
        context.EventQueued += observer;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm));
        context.EventQueued -= observer;
        Assert.AreEqual(firstRevision, firstInventory.Revision);
        Assert.AreEqual(secondRevision, secondInventory.Revision);
        Assert.AreSame(gift, firstInventory.Items.Single().Item);
        Assert.IsEmpty(secondInventory.Items);
        Assert.AreSame(firstOwner, gift.Source.Holder);
        Assert.IsFalse(session.IsClosed);
        Assert.IsEmpty(secondOwner.EventReceiver.History.OfType<TradeResultEvent>()
            .Where(result => result.Status == TradeStatus.Completed));
        firstOwner.EventReceiver.AddHandler(feature);
        Assert.AreEqual(TradeStatus.Completed,
            ChangeTrade(context, secondOwner, session, TradeAction.Confirm).Status);
        Assert.AreSame(gift, secondInventory.Items.Single().Item);
        TestContext.WriteLine($"A detached feature (trading={removeTrading}) failed actual trade preparation before mutation; restoring the same feature allowed a valid retry with the original item.");
    }

    private static TradeResultEvent ChangeTrade(
        Context context, Unit actor, TradeSession session, TradeAction action,
        IReadOnlyList<TradeItemOffer>? offers = null)
    {
        Guid operationId = Guid.NewGuid();
        context.Dispatch(context.Factory.NewEvent<TradeRequestEvent>() with
        {
            Actor = actor, Session = session, Action = action, ExpectedTermsRevision = session.TermsRevision,
            Offers = offers ?? [], OperationId = operationId, Receiver = actor.EventReceiver
        }).ToList();
        context.AdvanceTo(context.ContextTimeSpan);
        return actor.EventReceiver.History.OfType<TradeResultEvent>().Single(result => result.OperationId == operationId);
    }
}
