using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Parser;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Content.Tests;

[TestClass]
public sealed partial class DamageCatalogTest
{
    public TestContext TestContext { get; set; } = null!;

    private const string Types = """
        damageType:
          - id: impact
            name: Impact
            mitigation:
              formula: "raw / armor"
              fallback: "raw"
          - id: pure
        """;

    [TestMethod]
    public void SelectedModuleFieldsAreBoundPerCatalogAndDriveDamageEvents()
    {
        using ServiceProvider provider = CreateProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        DamageTypeRegistry half = Load(loader, Types, "armor-half");
        DamageTypeRegistry quarter = Load(loader, Types, "armor-quarter");

        Assert.AreNotSame(half, quarter);
        Assert.AreEqual("impact", half.Require(" IMPACT ").Id);
        Assert.IsNull(half.Require("pure").Formula);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((ICollection<DamageTypeDefinition>)half.All).Clear());
        Assert.AreEqual(20, Hit(half));
        Assert.AreEqual(10, Hit(quarter));
        Assert.AreEqual(20, Hit(half));
        Assert.HasCount(3, provider.GetServices<IContentModule>());
        TestContext.WriteLine("Two DI-selected catalogs resolved the same formula against separate schemas and dealt 20, 10, then 20 damage.");
    }

    [TestMethod]
    public void MissingFieldsOldFormatsAndDuplicateTypesFailWithSourceDiagnostics()
    {
        using ServiceProvider provider = CreateProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        var missingField = Assert.ThrowsExactly<InvalidDataException>(() =>
            Load(loader, Types));
        StringAssert.Contains(missingField.Message, "types.yaml");
        StringAssert.Contains(missingField.Message, "armor");

        var oldFormat = Assert.ThrowsExactly<InvalidDataException>(() =>
            Load(loader, "damageType:\n  - id: impact\n    factor: 2", "armor-half"));
        StringAssert.Contains(oldFormat.Message, "types.yaml");

        var badFallback = Assert.ThrowsExactly<InvalidDataException>(() =>
            Load(loader, """
                damageType:
                  - id: impact
                    mitigation: { formula: "raw", fallback: "true" }
                """, "armor-half"));
        StringAssert.Contains(badFallback.Message, "types.yaml");
        StringAssert.Contains(badFallback.Message, "impact");

        var duplicate = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("first.yaml", "damageType: [{ id: Impact }]"),
            Doc("duplicate.yaml", "damageType: [{ id: impact }]")
        ], [SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(duplicate.Message, "duplicate.yaml");
        StringAssert.Contains(duplicate.Message, "duplicate damage type");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Load(loader, Types, "armor-half", "armor-quarter"));
        Assert.AreEqual(20, Hit(Load(loader, Types, "armor-half")));
        TestContext.WriteLine("Unknown fields, legacy factors, invalid fallback types, duplicate IDs and field conflicts failed without contaminating later loads.");
    }

    [TestMethod]
    public void CrossModuleDamageReferencesResolveOnlyDeclaredTypes()
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent();
        services.AddSingleton<IContentModule, DamageReferenceModule>();
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        ContentCatalog catalog = loader.Load([
            Doc("reference.yaml", "damageRef: { type: impact }"),
            Doc("types.yaml", "damageType: [{ id: impact }]")
        ], ["damage-reference", SkillContentServiceCollectionExtensions.DamageModuleId]);
        Assert.AreEqual("impact", catalog.GetSection<DamageReference>("damageRef").Type);

        var missing = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("missing.yaml", "damageRef: { type: absent }"),
            Doc("types.yaml", "damageType: [{ id: impact }]")
        ], ["damage-reference", SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(missing.Message, "missing.yaml");
        StringAssert.Contains(missing.Message, "absent");
        TestContext.WriteLine("Cross-module damage references were resolved after the shared type catalog compiled.");
    }

    [TestMethod]
    [DataRow("Command", "{}", "None trigger")]
    [DataRow("None", "{ legacyOption: true }", "empty mapping")]
    [DataRow("None", "null", "empty mapping")]
    public void DeathContentRejectsActiveTriggersAndUnrecognizedConfiguration(string trigger, string data, string reason)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("mortality.yaml", $$"""
                damageType: [{ id: impact }]
                ability:
                  - id: mortality
                    type: Death
                    allowMultiple: false
                    trigger: { type: {{trigger}} }
                    data: {{data}}
                """)
        ], [SkillContentServiceCollectionExtensions.DamageModuleId, SkillContentServiceCollectionExtensions.AbilityModuleId]));
        StringAssert.Contains(error.Message, "mortality.yaml");
        StringAssert.Contains(error.Message, reason);
        TestContext.WriteLine("Invalid Death authoring was rejected at the catalog boundary: {0}", error.Message);
    }

    [TestMethod]
    public void DiAbilityCatalogCompilesOnceAndCreatesFreshBoundHandlers()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        AbilityCatalog half = LoadAbilities(loader, "armor-half");
        AbilityCatalog quarter = LoadAbilities(loader, "armor-quarter");

        IReadOnlyList<IHandler> first = half.Require("payload").CreateHandlers();
        IReadOnlyList<IHandler> second = half.Require("payload").CreateHandlers(2);
        var firstAbility = first.OfType<DamageAbility>().Single();
        var secondAbility = second.OfType<DamageAbility>().Single();
        Assert.AreNotSame(firstAbility, secondAbility);
        Assert.AreNotEqual(firstAbility.RuntimeInstanceId, secondAbility.RuntimeInstanceId);
        Assert.AreEqual(firstAbility.RuntimeInstanceId, first.OfType<TriggerHandler>().Single().TargetInstanceId);
        Assert.AreEqual(secondAbility.RuntimeInstanceId, second.OfType<TriggerHandler>().Single().TargetInstanceId);
        Assert.AreEqual(1, firstAbility.Level);
        Assert.AreEqual(2, secondAbility.Level);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, AbilityHandlerDefinition>)half.Definitions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)half.Require("payload").Content.Trigger.SourceAbilityIds).Clear());

        Assert.AreEqual(10, HitCatalog(half, 2));
        Assert.AreEqual(5, HitCatalog(quarter, 2));
        Assert.AreEqual(5, HitCatalog(half, null));
        TestContext.WriteLine("DI-loaded definitions created fresh, instance-bound handlers; separate schemas dealt 10, 5 and 5 damage.");
    }

    [TestMethod]
    public void LoadedProjectileCommandProducesAnActualHitAndDamageForEachCast()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"],
                [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        InstallCatalog(context, source, catalog, "delivery", 2);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        ProjectileAbility delivery = source.EventReceiver.GetHandlers<ProjectileAbility>().Single();
        DamageAbility payload = source.EventReceiver.GetHandlers<DamageAbility>().Single();
        Assert.AreEqual(2, target.EventReceiver.GetHandlers<BeingDamageAbility>().Single().Level);
        Guid deliveryGrant = Guid.NewGuid(), payloadGrant = Guid.NewGuid();
        var binding = new AtomicTransaction();
        delivery.Binding.StageBind(binding, new TestAbilitySource(deliveryGrant));
        delivery.Binding.StageAuthority(binding, context, source.Id);
        payload.Binding.StageBind(binding, new TestAbilitySource(payloadGrant));
        payload.Binding.StageAuthority(binding, context, source.Id);
        binding.Commit();

        for (int cast = 0; cast < 2; cast++)
        {
            context.Dispatch(context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver
            }).ToList();
            Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
            Assert.AreEqual(90 - cast * 10, target.Property.LifePoint);
        }

        CommandEvent[] concurrent = Enumerable.Range(0, 2).Select(_ =>
            context.Factory.NewEvent<CommandEvent>() with
            { AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver }).ToArray();
        foreach (CommandEvent command in concurrent)
            context.Dispatch(command).ToList();
        Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
        Assert.AreEqual(60, target.Property.LifePoint);
        foreach (CommandEvent command in concurrent)
        {
            var result = source.EventReceiver.History.OfType<UnitActionGateResultEvent>()
                .Single(ev => ev.Request.CommandId == command.CommandInstanceId);
            Assert.AreSame(result.Request.Causality, result.Causality);
            var hit = source.EventReceiver.History.OfType<ProjectileHitEvent>()
                .Single(ev => ev.CommandEventId == command.CommandInstanceId);
            CollectionAssert.AreEqual(new[] { "delivery" }, hit.TriggerChain!.AbilityIds.ToArray());
            Assert.AreEqual(delivery.RuntimeInstanceId, hit.AbilitySnapshot!.RuntimeInstanceId);
            Assert.AreEqual(2, hit.AbilitySnapshot.Level);
            Assert.AreSame(map, hit.HitMap);
            Assert.AreEqual(deliveryGrant, hit.AbilitySnapshot.GrantSourceInstanceId);
            Assert.AreEqual(2, ((AbilityTriggerChain)command.Causality!).TriggerCount);
        }
        Assert.AreNotSame(concurrent[0].Causality, concurrent[1].Causality);

        DamageLifeReduceEvent[] hits = target.EventReceiver.History.OfType<DamageLifeReduceEvent>().ToArray();
        CollectionAssert.AreEqual(new[] { 10, 10, 10, 10 }, hits.Select(hit => hit.LifePointReduce).ToArray());
        Assert.IsTrue(hits.All(hit => hit.DamageAbility.Ability is { } ability &&
            ability.RuntimeInstanceId == payload.RuntimeInstanceId && ability.GrantSourceInstanceId == payloadGrant &&
            ability.GrantSourceCategory == "Test" && ability.Level == 2));
        Assert.IsTrue(source.EventReceiver.History.OfType<ProjectileEvent>().All(projectile =>
            projectile.AbilitySnapshot?.RuntimeInstanceId == delivery.RuntimeInstanceId &&
            projectile.AbilitySnapshot.GrantSourceInstanceId == deliveryGrant &&
            projectile.AbilitySnapshot.GrantSourceCategory == "Test"));
        Assert.HasCount(4, source.EventReceiver.History.OfType<ProjectileHitEvent>());
        TestContext.WriteLine("Four DI-loaded commands each hit for 10; simultaneous casts retained independent chains through the shared action gate.");
    }

    [TestMethod]
    public void LoadedFiniteChargesGateActualProjectileCommandsAndRecover()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            gate: "{ occupiesWindup: false, resourceCost: 5, charges: { maximum: 2, initial: 1, recovery: { intervalSeconds: 2, mode: Serial } } }");
        ProjectileAbility first = catalog.Require("delivery").CreateHandlers().OfType<ProjectileAbility>().Single();
        ProjectileAbility second = catalog.Require("delivery").CreateHandlers().OfType<ProjectileAbility>().Single();
        Assert.AreNotSame(first.GateManager, second.GateManager);
        Assert.AreNotEqual(first.RuntimeInstanceId, second.RuntimeInstanceId);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"], [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        InstallCatalog(context, source, catalog, "delivery", 2);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        context.AdvanceTo(TimeSpan.Zero);
        ProjectileAbility delivery = source.EventReceiver.GetHandlers<ProjectileAbility>().Single();
        Assert.AreEqual(1, delivery.GateManager!.GetChargeSnapshot()!.Available);

        CommandEvent Cast()
        {
            var command = context.Factory.NewEvent<CommandEvent>() with
            { AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver };
            context.Dispatch(command).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
            return command;
        }

        CommandEvent accepted = Cast();
        CommandEvent rejected = Cast();
        Assert.AreEqual(CommandExecutionStatus.Success,
            source.EventReceiver.History.OfType<CommandExecutionEvent>().Single(ev => ev.Command == accepted).Status);
        Assert.AreEqual(CommandExecutionFailureReason.ChargesDepleted,
            source.EventReceiver.History.OfType<CommandExecutionEvent>().Single(ev => ev.Command == rejected).FailureReason);
        Assert.AreEqual(90, target.Property.LifePoint);
        Assert.AreEqual(95m, source.Property.Energy);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, delivery.GateManager.GetChargeSnapshot()!.Available);
        CommandEvent recovered = Cast();
        Assert.AreEqual(CommandExecutionStatus.Success,
            source.EventReceiver.History.OfType<CommandExecutionEvent>().Single(ev => ev.Command == recovered).Status);
        Assert.AreEqual(80, target.Property.LifePoint);
        TestContext.WriteLine("YAML-configured serial charges allowed one hit, explicitly rejected the next, then allowed a recovered hit at 2s.");
    }

    [TestMethod]
    public void LoadedHitTriggeredDamageUsesItsOwnFiniteCharges()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageGate: "{ occupiesWindup: false, resourceCost: 5, charges: { maximum: 1, initial: 1 } }");
        DamageAbility first = catalog.Require("payload").CreateHandlers().OfType<DamageAbility>().Single();
        DamageAbility second = catalog.Require("payload").CreateHandlers().OfType<DamageAbility>().Single();
        Assert.AreNotSame(first.GateManager, second.GateManager);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        context.AdvanceTo(TimeSpan.Zero);
        for (int i = 0; i < 2; i++)
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source,
                Target = target, Receiver = source.EventReceiver
            })).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }

        Assert.AreEqual(90, target.Property.LifePoint);
        Assert.AreEqual(95m, source.Property.Energy);
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        TestContext.WriteLine("A YAML-configured hit-triggered damage ability spent its charge once and rejected a second hit.");
    }

    [TestMethod]
    public void LoadedPeriodicDamageSettlesEveryConfiguredTick()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageGate: "{ occupiesWindup: false, charges: { maximum: 1, initial: 1 } }",
            damageTick: "{ tickCount: 3, tickIntervalSeconds: 1 }");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        context.AdvanceTo(TimeSpan.Zero);
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(100, target.Property.LifePoint);
        context.AdvanceTo(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEqual(new[] { 10, 10, 10 }, target.EventReceiver.History
            .OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        CollectionAssert.AreEqual(new[] { 1d, 2d, 3d }, target.EventReceiver.History
            .OfType<DamageLifeReduceEvent>().Select(ev => ev.Execute!.Value.TotalSeconds).ToArray());
        TestContext.WriteLine("A YAML-configured periodic damage effect settled 10 damage at seconds 1, 2 and 3.");
    }

    [TestMethod]
    [DataRow("DamageSkillHit")]
    [DataRow("BeingDamaged")]
    public void LoadedDamageRequestConditionsUseOriginalRolesAndTheListenersResources(string triggerType)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            gate: "{ resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }",
            projectileTrigger: $$"""
                { type: {{triggerType}}, sourceAbilityIds: [payload], eventSourceScope: Any,
                  conditions: [{ type: Expression, expression: 'event.source != holder && event.target != holder && event.ability.definition == "payload" && listener.ability.definition == "delivery"' }] }
                """);
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        foreach (Unit unit in new[] { source, observer, target }) context.AddEventReceiver(unit.EventReceiver);
        var map = new MapRuntime();
        Place(map, source, new Vector2D(0, 0));
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, new Vector2D(2, 0));
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 1);
        InstallCatalog(context, observer, catalog, "delivery", 1);
        context.AdvanceTo(TimeSpan.Zero);
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        observer.Property.EnergyRecoveryPerSecond = 0;
        int abilityLevel = source.EventReceiver.GetHandlers<DamageAbility>().Single().Level;
        int expectedDamage = abilityLevel * 10 / 2;
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source, Target = target,
            SourceObject = source.MapObject, TargetObject = target.MapObject, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { expectedDamage }, target.EventReceiver.History
            .OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        ProjectileEvent emission = observer.EventReceiver.History.OfType<ProjectileEvent>().Single();
        Assert.AreSame(observer, emission.Source);
        Assert.AreSame(observer.MapObject, emission.SourceObject);
        Assert.AreSame(target, emission.Target);
        Assert.AreEqual(15m, observer.Property.Energy);
        Assert.AreEqual(0, observer.EventReceiver.GetHandlers<ProjectileAbility>()
            .Single().GateManager!.GetChargeSnapshot()!.Available);
        TestContext.WriteLine($"YAML {triggerType} filtered the original damage roles and spent only the third-party responder's resources.");
    }

    [TestMethod]
    public void LoadedPeriodicDamageObservationsReleaseFromTheListeningOwnerOncePerTick()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            gate: "{ resourceCost: 5, occupiesWindup: false, charges: { maximum: 2, initial: 2 } }",
            damageTick: "{ tickCount: 2, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }",
            projectileTrigger: """
                { type: ScheduledDamageTick, sourceAbilityIds: [payload], eventSourceScope: Any,
                  conditions: [{ type: Expression, expression: 'event.source != holder && event.target != holder && event.ability.definition == "payload" && listener.ability.definition == "delivery"' }] }
                """);
        var factory = new TriggerChainFactory();
        using var context = new Context(causalityFactory: factory);
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        foreach (Unit unit in new[] { source, observer, target }) context.AddEventReceiver(unit.EventReceiver);
        var map = new MapRuntime();
        Place(map, source, new Vector2D(0, 0));
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, new Vector2D(2, 0));
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 1);
        InstallCatalog(context, observer, catalog, "delivery", 1);
        context.AdvanceTo(TimeSpan.Zero);
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        observer.Property.EnergyRecoveryPerSecond = 0;
        int abilityLevel = source.EventReceiver.GetHandlers<DamageAbility>().Single().Level;
        int expectedDamage = abilityLevel * 10 / 2;
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source, Target = target,
            SourceObject = source.MapObject, TargetObject = target.MapObject, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { expectedDamage, expectedDamage }, target.EventReceiver.History
            .OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        ProjectileEvent[] emissions = observer.EventReceiver.History.OfType<ProjectileEvent>().ToArray();
        Assert.HasCount(2, emissions);
        CollectionAssert.AreEqual(new[] { 1d, 2d }, emissions.Select(ev => ev.Execute!.Value.TotalSeconds).ToArray());
        Assert.IsTrue(emissions.All(ev => ev.Source == observer && ev.SourceObject == observer.MapObject &&
            ev.Target == target));
        Assert.AreEqual(10m, observer.Property.Energy);
        Assert.AreEqual(0, observer.EventReceiver.GetHandlers<ProjectileAbility>()
            .Single().GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine("Two YAML damage ticks settled once each and activated a third-party projectile response with independent resources; final quota was released.");
    }

    [TestMethod]
    public void LoadedPeriodicDamageCanStartImmediately()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageTick: "{ tickCount: 2, tickIntervalSeconds: 1, firstTickDelaySeconds: 0 }");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        context.AdvanceTo(TimeSpan.Zero);
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(90, target.Property.LifePoint);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(new[] { 10, 10 }, target.EventReceiver.History
            .OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        TestContext.WriteLine("An explicit zero first-tick delay settled immediately, followed by the configured one-second interval.");
    }

    [TestMethod]
    public void StatusPropertyExtensionsAreSelectedAndSealedPerCatalog()
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent();
        services.AddAbilityContent();
        var first = new StatusPropertyModule("status-ten", 10);
        var second = new StatusPropertyModule("status-twenty", 20);
        services.AddSingleton<IContentModule>(first);
        services.AddSingleton<IContentModule>(second);
        using ServiceProvider provider = services.BuildServiceProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        ContentDocument document = Doc("extended-status.yaml", """
            ability:
              - id: delivery
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
              - id: status
                type: Status
                allowMultiple: false
                trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                data:
                  modifiers: [{ target: module.reserve, formula: "module.bonus" }]
            """);
        AbilityCatalog Load(string module) => loader.Load([document],
            [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId, module])
            .GetSection<AbilityCatalog>("ability");
        AbilityCatalog ten = Load(first.Id);
        AbilityCatalog twenty = Load(second.Id);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Formulas!.RegisterNumber("late", _ => 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Targets!.Register(StatusPropertyModule.Target));
        Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([document],
            [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));

        int Apply(AbilityCatalog catalog)
        {
            using var context = new Context(causalityFactory: new TriggerChainFactory());
            context.Logger.Output = null;
            Unit owner = CreateUnit(context, "rule-owner");
            Unit target = CreateUnit(context, "target");
            context.AddEventReceiver(owner.EventReceiver);
            context.AddEventReceiver(target.EventReceiver);
            target.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory =
                (id, level) => catalog.Require(id).CreateHandlers(level);
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = null, Target = target, AbilityIds = ["status"],
                Purpose = AbilityInstallPurpose.StatusApplication,
                ApplicationOrigin = StatusApplicationOrigin.FromRule(owner.ActionGate!, owner.EventReceiver),
                Receiver = target.EventReceiver
            }).ToList();
            context.AdvanceTo(TimeSpan.Zero);
            return target.Property.EnergyMax;
        }
        Assert.AreEqual(110, Apply(ten));
        Assert.AreEqual(120, Apply(twenty));
        TestContext.WriteLine("Catalog-local status extensions applied distinct +10 and +20 bonuses and rejected unselected or late registration.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PeriodicSelectorAppliesIndependentEffectsToEachValidTarget(bool gated)
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var battle = CreatePeriodicSelectorBattle(gated, 8, diagnostics);
        using Context context = battle.Context;
        DispatchPeriodicHit(battle, new Vector2D(10, 10));
        context.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(90, battle.First.Property.LifePoint);
        Assert.AreEqual(90, battle.Second.Property.LifePoint);
        Assert.AreEqual(100, battle.Outside.Property.LifePoint);
        Assert.AreEqual(2, battle.Factory.GetActivePeriodicEffectCount(context));
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        foreach (Unit target in new[] { battle.First, battle.Second })
        {
            CollectionAssert.AreEqual(new[] { 10, 10 }, target.EventReceiver.History
                .OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
            CollectionAssert.AreEqual(new[] { 0d, 1d }, target.EventReceiver.History
                .OfType<DamageLifeReduceEvent>().Select(ev => ev.Execute!.Value.TotalSeconds).ToArray());
            Assert.HasCount(1, target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>());
        }
        Assert.AreEqual(0, battle.Factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(diagnostics);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.Status == CommandExecutionStatus.Success && ev.ActivationId != Guid.Empty));
        if (gated)
        {
            Assert.AreEqual(0, battle.Ability.GateManager!.GetChargeSnapshot()!.Available);
            Assert.AreEqual(15m, battle.Source.Property.Energy);
        }
        TestContext.WriteLine($"With gate={gated}, two selected targets independently received 10 damage at seconds 0 and 1; the outside unit was unaffected.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PeriodicSelectorCapacityFailureLeavesAllTargetsAndChargesUntouched(bool gated)
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        var battle = CreatePeriodicSelectorBattle(gated, 1, diagnostics);
        using Context context = battle.Context;
        DispatchPeriodicHit(battle, new Vector2D(10, 10));
        context.AdvanceTo(TimeSpan.FromSeconds(2));

        Assert.AreEqual(100, battle.First.Property.LifePoint);
        Assert.AreEqual(100, battle.Second.Property.LifePoint);
        Assert.AreEqual(0, battle.Factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(context.History.OfType<PeriodicEffectStartedEvent>());
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.ActivePeriodicEffects, diagnostics[0].Reason);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.PeriodicEffectLimit));
        if (gated)
        {
            Assert.AreEqual(1, battle.Ability.GateManager!.GetChargeSnapshot()!.Available);
            Assert.AreEqual(20m, battle.Source.Property.Energy);
        }
        TestContext.WriteLine($"With gate={gated}, capacity one rejected both selected targets without reserving effects or consuming resources.");
    }

    [TestMethod]
    public void PeriodicSelectorWithoutEligibleTargetsDoesNotConsumeTheGate()
    {
        var battle = CreatePeriodicSelectorBattle(true, 8, []);
        using Context context = battle.Context;
        DispatchPeriodicHit(battle, new Vector2D(50, 50));
        context.AdvanceTo(TimeSpan.Zero);

        Assert.AreEqual(0, battle.Factory.GetActivePeriodicEffectCount(context));
        Assert.AreEqual(1, battle.Ability.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, battle.Source.Property.Energy);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.TargetNotExist));
        TestContext.WriteLine("An empty periodic selector failed before spending a charge or energy.");
    }

    [TestMethod]
    public void PeriodicSelectorRollsBackEveryReservationIfResourcesChangeDuringWindup()
    {
        var battle = CreatePeriodicSelectorBattle(true, 8, [], windupSeconds: 1);
        using Context context = battle.Context;
        var publishedCounts = new List<int>();
        context.EventProcessed += _ => publishedCounts.Add(battle.Factory.GetActivePeriodicEffectCount(context));
        DispatchPeriodicHit(battle, new Vector2D(10, 10));
        context.AdvanceTo(TimeSpan.Zero);
        battle.Source.Property.Energy = 0;
        context.AdvanceTo(TimeSpan.FromSeconds(1));

        Assert.AreEqual(100, battle.First.Property.LifePoint);
        Assert.AreEqual(100, battle.Second.Property.LifePoint);
        Assert.AreEqual(0, battle.Factory.GetActivePeriodicEffectCount(context));
        Assert.IsTrue(publishedCounts.All(count => count == 0));
        Assert.AreEqual(1, battle.Ability.GateManager!.GetChargeSnapshot()!.Available);
        Assert.HasCount(1, battle.Source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.ResourceInsufficient));
        Assert.IsEmpty(context.History.OfType<PeriodicEffectStartedEvent>());
        TestContext.WriteLine("Resource loss during windup rolled back both unpublished periodic reservations and left the charge intact.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReleasedPeriodicSelectorEffectsSurviveUpstreamCancellation(bool gated)
    {
        var battle = CreatePeriodicSelectorBattle(gated, 8, []);
        using Context context = battle.Context;
        using var cancellation = new CancellationTokenSource();
        DispatchPeriodicHit(battle, new Vector2D(10, 10), cancellation);
        context.AdvanceTo(TimeSpan.Zero);
        cancellation.Cancel();
        context.AdvanceTo(TimeSpan.FromSeconds(1));

        Assert.AreEqual(80, battle.First.Property.LifePoint);
        Assert.AreEqual(80, battle.Second.Property.LifePoint);
        Assert.AreEqual(0, battle.Factory.GetActivePeriodicEffectCount(context));
        TestContext.WriteLine($"With gate={gated}, upstream cancellation after release did not cancel either independent periodic effect.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PeriodicSelectorNotificationFailureReportsTheCommittedActivation(bool gated)
    {
        var battle = CreatePeriodicSelectorBattle(gated, 8, []);
        using Context context = battle.Context;
        AbilityActivationResultEvent? observed = null;
        context.EventProcessed += ev =>
        {
            if (ev is AbilityActivationResultEvent { Status: CommandExecutionStatus.Success } result)
            {
                observed = result;
                throw new InvalidOperationException("notification failed");
            }
        };
        DispatchPeriodicHit(battle, new Vector2D(10, 10));
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));

        Assert.IsNotNull(observed);
        Assert.AreEqual(observed.ActivationId, error.OperationId);
        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        Assert.AreEqual(2, battle.Factory.GetActivePeriodicEffectCount(context));
        if (gated)
        {
            Assert.AreEqual(0, battle.Ability.GateManager!.GetChargeSnapshot()!.Available);
            Assert.AreEqual(15m, battle.Source.Property.Energy);
        }
        TestContext.WriteLine($"With gate={gated}, a failed activation notification identified the already committed operation and retained both reservations.");
    }

    [TestMethod]
    public void LoadedPeriodicDamageRequiresPositiveTicksAndInterval()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        foreach (string tick in new[]
        {
            "{ tickCount: 0, tickIntervalSeconds: 1 }",
            "{ tickCount: 2, tickIntervalSeconds: 0 }",
            "{ tickCount: 2 }"
        })
        {
            var failure = Assert.ThrowsExactly<InvalidDataException>(() =>
                LoadAbilities(loader, "armor-half", damageTick: tick));
            StringAssert.Contains(failure.Message, "payload.yaml");
            StringAssert.Contains(failure.Message, "positive");
        }
        var invalidFirstDelay = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadAbilities(loader, "armor-half",
                damageTick: "{ tickCount: 2, tickIntervalSeconds: 1, firstTickDelaySeconds: -1 }"));
        StringAssert.Contains(invalidFirstDelay.Message, "firstTickDelaySeconds");
        TestContext.WriteLine("Invalid periodic counts and intervals were rejected at catalog compilation.");
    }

    [TestMethod]
    public void LoadedProjectileCanActivateFromAHitAndUseCharges()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            gate: "{ occupiesWindup: false, charges: { maximum: 1, initial: 1 } }",
            projectileTrigger: "{ type: ProjectileHit, sourceAbilityIds: [payload] }");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, source, catalog, "delivery", 1);
        context.AdvanceTo(TimeSpan.Zero);
        for (int i = 0; i < 2; i++)
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "payload", Source = source,
                Target = target, Receiver = source.EventReceiver
            })).ToList();
            context.AdvanceTo(context.ContextTimeSpan);
        }

        Assert.HasCount(1, source.EventReceiver.History.OfType<ProjectileEvent>()
            .Where(projectile => projectile.AbilityId == "delivery"));
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        TestContext.WriteLine("A YAML-configured hit trigger launched one projectile and rejected its depleted second activation.");
    }

    [TestMethod]
    public void LoadedFiniteChargesRequireInitialCountAndExplicitRecoveryMode()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        var missingInitial = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadAbilities(loader, "armor-half", gate: "{ charges: { maximum: 3 } }"));
        StringAssert.Contains(missingInitial.Message, "delivery.yaml");
        var missingMode = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadAbilities(loader, "armor-half", gate: "{ charges: { maximum: 3, initial: 0, recovery: { intervalSeconds: 2 } } }"));
        StringAssert.Contains(missingMode.Message, "delivery.yaml");
        var missingDamageInitial = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadAbilities(loader, "armor-half", damageGate: "{ charges: { maximum: 3 } }"));
        StringAssert.Contains(missingDamageInitial.Message, "payload.yaml");
        StringAssert.Contains(missingDamageInitial.Message, "initial count");
        TestContext.WriteLine("Finite charge content without an initial count or recovery mode failed during catalog loading.");
    }

    [TestMethod]
    public void TravelingProjectileCannotResetItsCommandTriggerBudget()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(
            provider.GetRequiredService<IContentCatalogLoader>(), "armor-half", projectileSpeed: 5);
        var diagnostics = new List<TriggerLimitDiagnostic>();
        using var context = new Context(causalityFactory: new TriggerChainFactory(
            totalBudget: 1, mode: TriggerLimitMode.Production, report: diagnostics.Add));
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"],
                [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        InstallCatalog(context, source, catalog, "delivery", 2);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        context.RunUntil(TimeSpan.Zero);
        var command = context.Factory.NewEvent<CommandEvent>() with
        { AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver };
        context.Dispatch(command).ToList();

        context.RunUntil(TimeSpan.FromSeconds(1));
        Assert.AreEqual(100, target.Property.LifePoint);
        context.RunUntil(TimeSpan.FromSeconds(3));

        ProjectileHitEvent hit = source.EventReceiver.History.OfType<ProjectileHitEvent>().Single();
        Assert.IsGreaterThan(TimeSpan.FromSeconds(1), hit.Execute!.Value);
        Assert.AreEqual(100, target.Property.LifePoint);
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.TotalBudget, diagnostics[0].Reason);
        Assert.AreEqual(1, ((AbilityTriggerChain)command.Causality!).TriggerCount);
        CollectionAssert.AreEqual(new[] { "delivery" }, hit.TriggerChain!.AbilityIds.ToArray());
        TestContext.WriteLine("A moving projectile hit after one second but could not bypass its originating command's exhausted trigger budget.");
    }

    [TestMethod]
    public void AbilityCatalogRejectsInvalidOrUnselectedDefinitionsWithSources()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        string[] modules = [SkillContentServiceCollectionExtensions.AbilityModuleId,
            SkillContentServiceCollectionExtensions.DamageModuleId];

        var missingAllow = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("missing-allow.yaml", """
                ability:
                  - id: payload
                    type: Damage
                    trigger: { type: ProjectileHit }
                """)
        ], modules));
        StringAssert.Contains(missingAllow.Message, "missing-allow.yaml");
        StringAssert.Contains(missingAllow.Message, "allowMultiple");

        var scalarTrigger = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("scalar-trigger.yaml", """
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: ProjectileHit
                """)
        ], modules));
        StringAssert.Contains(scalarTrigger.Message, "scalar-trigger.yaml");

        var invalidFormula = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("invalid-formula.yaml", """
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: { type: ProjectileHit }
                    data:
                      damage: [{ type: impact, formula: "unknownField + 1" }]
                """),
            Doc("types.yaml", "damageType: [{ id: impact }]")
        ], modules));
        StringAssert.Contains(invalidFormula.Message, "invalid-formula.yaml");
        StringAssert.Contains(invalidFormula.Message, "unknownField");

        var legacyFactor = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("legacy-factor.yaml", """
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: { type: ProjectileHit }
                    data:
                      damage: [{ type: impact, factor: 5 }]
                """),
            Doc("types.yaml", "damageType: [{ id: impact }]")
        ], modules));
        StringAssert.Contains(legacyFactor.Message, "legacy-factor.yaml");

        var missingReference = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("missing-reference.yaml", """
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: { type: ProjectileHit, sourceAbilityIds: [absent] }
                    data:
                      damage: [{ type: impact, formula: "10" }]
                """),
            Doc("types.yaml", "damageType: [{ id: impact }]")
        ], modules));
        StringAssert.Contains(missingReference.Message, "missing-reference.yaml");
        StringAssert.Contains(missingReference.Message, "absent");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            loader.Load([], [SkillContentServiceCollectionExtensions.AbilityModuleId]));
        Assert.AreEqual(10, HitCatalog(LoadAbilities(loader, "armor-half"), 2));
        TestContext.WriteLine("Invalid headers, legacy fields, formulas and missing references failed without contaminating the next catalog.");
    }

    [TestMethod]
    public void LoadedConditionalChargeRecoveryUsesRealSignalsAndInstanceTimers()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("recovery.yaml", """
                ability:
                  - id: charged
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                    data:
                      gate:
                        charges:
                          maximum: 3
                          initial: 0
                          recovery: { intervalSeconds: 10, mode: Parallel }
                          conditionalRecovery:
                            - amount: 1
                              trigger:
                                type: Signal
                                signalId: recharge
                                conditions:
                                  - { type: Expression, expression: 'event.source == holder && event.target == holder && listener.ability.definition == "charged"' }
                            - amount: 5
                              trigger: { type: Signal, signalId: fill }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit first = CreateUnit(context, "first");
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(first.EventReceiver);
        context.AddEventReceiver(second.EventReceiver);
        InstallCatalog(context, first, catalog, "charged", 1);
        InstallCatalog(context, second, catalog, "charged", 1);
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        ProjectileAbility ability = first.EventReceiver.GetHandlers<ProjectileAbility>().Single();
        ProjectileAbility other = second.EventReceiver.GetHandlers<ProjectileAbility>().Single();
        CommandGateManager gate = ability.GateManager!;

        void Signal(string signal, Unit? target = null) => context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            SignalId = signal, CommandEventId = Guid.NewGuid(), Source = first, Target = target ?? first,
            Receiver = first.EventReceiver
        }).ToList();
        Signal("recharge", second);
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(first.EventReceiver.History.OfType<AbilityChargeRestoreResultEvent>());
        Signal("recharge");
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(2, gate.GetChargeSnapshot()!.Recoveries.Single().Count);
        Assert.AreEqual(0, other.GateManager!.GetChargeSnapshot()!.Available);
        Signal("fill");
        context.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.AreEqual(3, gate.GetChargeSnapshot()!.Available);
        Assert.IsEmpty(gate.GetChargeSnapshot()!.Recoveries);
        Assert.IsEmpty(first.EventReceiver.History.OfType<ProjectileEvent>());
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        Guid activation = Guid.NewGuid();
        Assert.IsTrue(gate.ValidateAndReserve(context.ContextTimeSpan, null, activation).CanExecute);
        Assert.AreEqual(CommandReleaseResult.Committed, gate.CommitRelease(first, activation, context.ContextTimeSpan));
        context.AdvanceTo(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, gate.GetChargeSnapshot()!.Available);
        Assert.AreEqual(3, other.GateManager!.GetChargeSnapshot()!.Available);
        context.AdvanceTo(TimeSpan.FromSeconds(14));
        Assert.AreEqual(3, gate.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("Two configured signals restored one selected instance; canceled parallel timers did not refill a later use.");
    }

    [TestMethod]
    [DataRow("{ amount: 0, trigger: { type: Signal, signalId: pulse } }")]
    [DataRow("{ amount: 1 }")]
    [DataRow("{ amount: 1, trigger: { type: None } }")]
    [DataRow("{ amount: 1, trigger: { type: Signal, signalId: pulse, conditions: [{ type: Expression, expression: 'unknown > 0' }] } }")]
    [DataRow("{ amount: 1, trigger: { type: Command, sourceAbilityIds: [missing] } }")]
    public void InvalidConditionalChargeRecoveryFailsLoading(string recovery)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("invalid-recovery.yaml", $$"""
                    ability:
                      - id: charged
                        type: Projectile
                        allowMultiple: false
                        trigger: { type: Command }
                        data:
                          gate:
                            charges:
                              maximum: 2
                              initial: 0
                              conditionalRecovery: [{{recovery}}]
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-recovery.yaml");
        TestContext.WriteLine($"Invalid conditional recovery was rejected during loading: {recovery}");
    }

    [TestMethod]
    public void LoadedHealUsesAnIndependentGateForEachInstalledInstance()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("heal.yaml", """
                ability:
                  - id: heal-delivery-fixture
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                  - id: heal-fixture
                    type: Heal
                    allowMultiple: false
                    trigger: { type: ProjectileHit, sourceAbilityIds: [heal-delivery-fixture] }
                    data:
                      amount: 7
                      gate: { windupSeconds: 2, resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit first = CreateUnit(context, "first");
        Unit second = CreateUnit(context, "second");
        context.AddEventReceiver(first.EventReceiver);
        context.AddEventReceiver(second.EventReceiver);
        first.Property.LifePoint = second.Property.LifePoint = 50;
        first.Property.EnergyMax = second.Property.EnergyMax = 20;
        first.Property.Energy = second.Property.Energy = 20;
        first.Abilities.BaseProperty.EnergyRecoveryPerSecond = second.Abilities.BaseProperty.EnergyRecoveryPerSecond = 0;
        first.Property.EnergyRecoveryPerSecond = second.Property.EnergyRecoveryPerSecond = 0;
        InstallCatalog(context, first, catalog, "heal-fixture", 2);
        InstallCatalog(context, second, catalog, "heal-fixture", 3);
        context.AdvanceTo(TimeSpan.Zero);
        HealAbility firstHeal = first.EventReceiver.GetHandlers<HealAbility>().Single();
        HealAbility secondHeal = second.EventReceiver.GetHandlers<HealAbility>().Single();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
            {
                CommandEventId = Guid.NewGuid(), AbilityId = "heal-delivery-fixture",
                Source = first, Target = first, Receiver = first.EventReceiver
            })).ToList();
            context.AdvanceTo(TimeSpan.Zero);
        }

        Assert.AreNotSame(firstHeal, secondHeal);
        Assert.AreEqual(2, firstHeal.Level);
        Assert.AreEqual(3, secondHeal.Level);
        Assert.IsEmpty(first.EventReceiver.History.OfType<HealEvent>());
        firstHeal.Level = 4;
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        HealEvent released = first.EventReceiver.History.OfType<HealEvent>().Single();
        Assert.AreEqual(4, released.HealAbility.Ability!.Level);
        firstHeal.Level = 5;
        Assert.AreEqual(4, released.HealAbility.Ability.Level);
        Assert.AreEqual(57, first.Property.LifePoint);
        Assert.AreEqual(15m, first.Property.Energy);
        Assert.AreEqual(0, firstHeal.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(50, second.Property.LifePoint);
        Assert.AreEqual(20m, second.Property.Energy);
        Assert.AreEqual(1, secondHeal.GateManager!.GetChargeSnapshot()!.Available);
        Assert.HasCount(1, first.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(ev => ev.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        TestContext.WriteLine("Loaded heals kept independent levels and gates; windup used the upgraded level and release preserved it.");
    }

    [TestMethod]
    public void LoadedPassivePropertyUsesLiveOwnerLevelAndUninstalls()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("passive.yaml", """
                ability:
                  - id: passive
                    type: BaseProperty
                    allowMultiple: false
                    trigger: { type: None }
                    data:
                      modifiers:
                        - { target: lifePointMax, formula: "ability.level * 10 + source.level + target.level" }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit unit = CreateUnit(context, "unit");
        context.AddEventReceiver(unit.EventReceiver);
        InstallCatalog(context, unit, catalog, "passive", 2);
        context.AdvanceTo(TimeSpan.Zero);
        PropertyAbility passive = unit.EventReceiver.GetHandlers<PropertyAbility>().Single();
        Guid instance = passive.RuntimeInstanceId;
        Assert.AreEqual(122, unit.Property.LifePointMax);
        Assert.IsEmpty(unit.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));

        unit.SetLevel(4);
        Assert.AreEqual(128, unit.Property.LifePointMax);
        Assert.AreEqual(2, passive.Level);
        passive.Level = 3;
        unit.SetLevel(5);
        Assert.AreEqual(140, unit.Property.LifePointMax);
        Assert.AreEqual(instance, passive.RuntimeInstanceId);
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = unit, Target = unit,
            AbilityIds = ["passive"], Receiver = unit.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(100, unit.Property.LifePointMax);
        Assert.IsEmpty(unit.EventReceiver.GetHandlers<PropertyAbility>());
        TestContext.WriteLine("Passive property used live owner level, preserved its ability identity across upgrades, and was removed atomically.");
    }

    [TestMethod]
    public void FailedPassiveFormulaLeavesExistingProjectionAndHandlersUnchanged()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("atomic-passive.yaml", """
                ability:
                  - id: existing
                    type: BaseProperty
                    allowMultiple: false
                    trigger: { type: None }
                    data: { modifiers: [{ target: lifePointMax, formula: "10" }] }
                  - id: failing
                    type: BaseProperty
                    allowMultiple: false
                    trigger: { type: None }
                    data: { modifiers: [{ target: lifePointMax, formula: "10 / target.energy", fallback: "7" }] }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit unit = CreateUnit(context, "unit");
        context.AddEventReceiver(unit.EventReceiver);
        unit.Property.Energy = 0;
        InstallCatalog(context, unit, catalog, "existing", 1);
        context.AdvanceTo(TimeSpan.Zero);
        PropertyAbility original = unit.EventReceiver.GetHandlers<PropertyAbility>().Single();
        Assert.AreEqual(110, unit.Property.LifePointMax);

        InstallCatalog(context, unit, catalog, "failing", 1);
        Assert.ThrowsExactly<ExpressionEvaluationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(110, unit.Property.LifePointMax);
        Assert.AreSame(original, unit.EventReceiver.GetHandlers<PropertyAbility>().Single());
        Assert.IsFalse(unit.EventReceiver.History.OfType<AbilityInstalledEvent>()
            .Any(ev => ev.AbilityIds.Contains("failing")));
        TestContext.WriteLine("A failing passive formula preserved the existing handler and projection without publishing installation.");
    }

    [TestMethod]
    public void PassiveAndStatusModifiersSharePriorityOrder()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("ordered-properties.yaml", """
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                  - id: passive
                    type: BaseProperty
                    priority: -1
                    allowMultiple: false
                    trigger: { type: None }
                    data: { modifiers: [{ target: lifePointMax, formula: "10" }] }
                  - id: status
                    type: Status
                    allowMultiple: false
                    trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                    data: { modifiers: [{ target: lifePointMax, formula: "target.lifeMax / 10" }] }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, target, catalog, "passive", 1);
        context.AdvanceTo(TimeSpan.Zero);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target, AbilityIds = ["status"],
            Purpose = AbilityInstallPurpose.StatusApplication,
            ApplicationOrigin = StatusApplicationOrigin.FromRule(target.ActionGate!, target.EventReceiver),
            Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(121, target.Property.LifePointMax);
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target,
            AbilityIds = ["passive"], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(110, target.Property.LifePointMax);
        TestContext.WriteLine("The earlier +10 passive precedes the 10% status; removing it recalculates the status from base.");
    }

    [TestMethod]
    [DataRow("{ modifiers: [{ target: lifePointMax, factor: 10 }] }")]
    [DataRow("{ modifiers: [{ target: lifePointMax, factors: [10] }] }")]
    [DataRow("{ modifiers: [] }")]
    public void InvalidPassivePropertyContentFailsAtLoad(string data)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("invalid-passive.yaml", $$"""
                    ability:
                      - id: passive
                        type: BaseProperty
                        allowMultiple: false
                        trigger: { type: None }
                        data: {{data}}
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-passive.yaml");
        TestContext.WriteLine($"Rejected passive data: {data}");
    }

    [TestMethod]
    public void LoadedStatusUsesExpressionStackingGateAndExpiry()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("status.yaml", """
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                  - id: status
                    type: Status
                    allowMultiple: false
                    trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                    data:
                      durationSeconds: 5
                      stackPolicy: StackOnly
                      stackLimit: 2
                      modifiers:
                        - { target: lifePointMax, formula: "ability.level * 10 + target.level" }
                      gate: { charges: { maximum: 2, initial: 2 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"], [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        target.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory =
            (id, level) => catalog.Require(id).CreateHandlers(level);
        InstallCatalog(context, source, catalog, "delivery", 1);
        InstallCatalog(context, source, catalog, "status", 2);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility listener = source.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.IsEmpty(source.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));

        for (int cast = 0; cast < 3; cast++)
        {
            context.AdvanceTo(TimeSpan.FromSeconds(cast));
            context.Dispatch(context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver
            }).ToList();
            context.AdvanceTo(TimeSpan.FromSeconds(cast));
            Assert.AreEqual(cast == 0 ? 121 : 142, target.Property.LifePointMax);
        }

        StatusAbility applied = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        Assert.AreEqual(2, applied.CurrentStack);
        Assert.AreEqual(2, applied.ApplicationOrigin!.AbilitySnapshot!.Level);
        Assert.AreEqual(0, listener.GateManager!.GetChargeSnapshot()!.Available);
        Assert.HasCount(1, source.EventReceiver.History.OfType<AbilityActivationResultEvent>()
            .Where(result => result.AbilityId == listener.Id &&
                result.FailureReason == CommandExecutionFailureReason.ChargesDepleted));
        Assert.HasCount(1, target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        listener.Level = 5;
        target.SetLevel(4);
        Assert.AreEqual(148, target.Property.LifePointMax);
        Assert.AreEqual(2, applied.Level);
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("YAML status gained 21 life maximum per stack, stopped at two charges and expired at second 5.");
    }

    [TestMethod]
    public void SignalTriggeredStatusUsesResponderAndExpires()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("signal-status.yaml", """
                ability:
                  - id: signal-status
                    type: Status
                    allowMultiple: false
                    trigger:
                      type: Signal
                      signalId: pulse
                      eventSourceScope: Any
                      conditions:
                        - { type: Expression, expression: "event.source == null && event.target != holder" }
                    data:
                      durationSeconds: 2
                      modifiers:
                        - { target: lifePointMax, formula: "ability.level * 10" }
                      gate: { charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(observer.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        target.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory =
            (id, level) => catalog.Require(id).CreateHandlers(level);
        InstallCatalog(context, observer, catalog, "signal-status", 2);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility listener = observer.EventReceiver.GetHandlers<StatusAbility>().Single();
        void Signal(string signalId, Unit? source) =>
            context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
            {
                SignalId = signalId, CommandEventId = Guid.NewGuid(), Source = source, Target = target,
                Receiver = (source ?? target).EventReceiver
            }).ToList();

        Signal("other", null);
        Signal("pulse", observer);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.AreEqual(1, listener.GateManager!.GetChargeSnapshot()!.Available);
        Signal("pulse", null);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility applied = target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status).OfType<StatusAbility>().Single();
        Assert.AreEqual(120, target.Property.LifePointMax);
        Assert.AreSame(observer, applied.ApplicationSource);
        Assert.AreSame(listener, applied.ApplicationOrigin!.Ability);
        Assert.AreEqual(0, listener.GateManager.GetChargeSnapshot()!.Available);
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(100, target.Property.LifePointMax);
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        TestContext.WriteLine("Only the matching actorless signal applied the responder's level-2 status; its lease expired.");
    }

    [TestMethod]
    public void PeriodicStatusContentCapturesReleaseLevelAndReadsLiveTargetProperties()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("periodic-status.yaml", """
                damageType: [{ id: pure }]
                ability:
                  - id: receiver
                    type: BeingDamage
                    allowMultiple: false
                    trigger: { type: None }
                  - id: periodic-status
                    type: Status
                    allowMultiple: false
                    trigger: { type: Signal, signalId: pulse, eventSourceScope: Any }
                    data:
                      durationSeconds: 6
                      stackPolicy: Independent
                      gate: { windupSeconds: 1, charges: { maximum: 1, initial: 1 } }
                      periodicEffects:
                        - type: Damage
                          tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
                          damage: [{ type: pure, formula: "ability.level * 2 + target.level" }]
                        - type: Heal
                          tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
                          amount: 2
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId,
            SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, source, catalog, "periodic-status", 1);
        InstallCatalog(context, target, catalog, "receiver", 1);
        context.AdvanceTo(TimeSpan.Zero);
        target.Property.LifePoint = 80;
        StatusAbility listener = source.EventReceiver.GetHandlers<StatusAbility>().Single();
        context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            SignalId = "pulse", CommandEventId = Guid.NewGuid(), Source = null, Target = target,
            Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        listener.Level = 3;
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        listener.Level = 9;
        int[] expectedDamage = [3 * 2 + target.Property.Level, 3 * 2 + 4, 3 * 2 + 4];
        context.AdvanceTo(TimeSpan.FromSeconds(2));
        Assert.AreEqual(75, target.Property.LifePoint);
        target.SetLevel(4);
        context.AdvanceTo(TimeSpan.FromSeconds(4));
        CollectionAssert.AreEqual(expectedDamage,
            target.EventReceiver.History.OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 2, 2 },
            target.EventReceiver.History.OfType<HealLifeGainEvent>().Select(ev => ev.LifeGain).ToArray());
        ScheduledDamageTickEvent[] ticks = target.EventReceiver.History.OfType<ScheduledDamageTickEvent>().ToArray();
        CollectionAssert.AreEqual(new[] { 2d, 3d, 4d }, ticks.Select(ev => ev.Execute!.Value.TotalSeconds).ToArray());
        Assert.IsTrue(ticks.All(ev => ev.Damage.Level == 3 && ev.Damage.Ability!.Level == 3 &&
            ev.Damage.Ability.RuntimeInstanceId == listener.RuntimeInstanceId && ReferenceEquals(ev.Source, source)));
        Assert.AreEqual(59, target.Property.LifePoint);
        Assert.AreEqual(0, listener.GateManager!.GetChargeSnapshot()!.Available);
        context.AdvanceTo(TimeSpan.FromSeconds(7));
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        TestContext.WriteLine("Release level 3 survived later level 9; live target levels produced damage 7,10,10 and healing 2,2,2 at seconds 2,3,4.");
    }

    [TestMethod]
    public void ActorlessPeriodicStatusContentSettlesBothEffectsAndStopsOnExpiry()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("rule-periodic-status.yaml", """
                damageType: [{ id: pure }]
                ability:
                  - id: receiver
                    type: BeingDamage
                    allowMultiple: false
                    trigger: { type: None }
                  - id: periodic-status
                    type: Status
                    allowMultiple: false
                    trigger: { type: None }
                    data:
                      durationSeconds: 2
                      stackPolicy: Independent
                      periodicEffects:
                        - type: Damage
                          tick: { tickCount: 5, tickIntervalSeconds: 1 }
                          damage: [{ type: pure, formula: "ability.level + 3" }]
                        - type: Heal
                          tick: { tickCount: 5, tickIntervalSeconds: 1 }
                          amount: 2
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit owner = CreateUnit(context, "rule-owner");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(owner.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, target, catalog, "receiver", 1);
        context.AdvanceTo(TimeSpan.Zero);
        target.Property.LifePoint = 80;
        StatusApplicationOrigin origin = StatusApplicationOrigin.FromRule(owner.ActionGate!, owner.EventReceiver);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = target, AbilityIds = ["periodic-status"],
            Level = 2, Purpose = AbilityInstallPurpose.StatusApplication,
            ApplicationOrigin = origin, Receiver = target.EventReceiver
        }).ToList();
        int expectedDamage = 2 + 3;
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { expectedDamage, expectedDamage },
            target.EventReceiver.History.OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 2 },
            target.EventReceiver.History.OfType<HealLifeGainEvent>().Select(ev => ev.LifeGain).ToArray());
        ScheduledDamageTickEvent[] damage = target.EventReceiver.History.OfType<ScheduledDamageTickEvent>().ToArray();
        ScheduledHealTickEvent[] healing = target.EventReceiver.History.OfType<ScheduledHealTickEvent>().ToArray();
        Assert.IsTrue(damage.All(ev => ev.Source == null && ev.Damage.Ability == null &&
            ReferenceEquals(ev.ApplicationOrigin, origin)));
        Assert.IsTrue(healing.All(ev => ev.Source == null && ev.Heal.Ability == null &&
            ReferenceEquals(ev.ApplicationOrigin, origin)));
        Assert.AreEqual(74, target.Property.LifePoint);
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        TestContext.WriteLine("A real rule with no actor applied damage 5 and healing 2 twice; expiry canceled the remaining three ticks.");
    }

    [TestMethod]
    public void PeriodicStatusContentQuotaRejectionDoesNotSpendGate()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("quota-periodic-status.yaml", """
                ability:
                  - id: periodic-status
                    type: Status
                    allowMultiple: false
                    trigger: { type: Signal, signalId: pulse, eventSourceScope: Any }
                    data:
                      durationSeconds: 5
                      gate: { cooldownSeconds: 3, resourceCost: 5, charges: { maximum: 1, initial: 1 } }
                      periodicEffects:
                        - { type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 1 }, amount: 2 }
                        - { type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 1 }, amount: 2 }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        var diagnostics = new List<TriggerLimitDiagnostic>();
        using var context = new Context(causalityFactory: new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add, maxActivePeriodicEffects: 1));
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        source.Abilities.BaseProperty.Energy = 20;
        source.Abilities.BaseProperty.EnergyMax = 20;
        source.Abilities.ReApply();
        InstallCatalog(context, source, catalog, "periodic-status", 1);
        target.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory =
            (id, level) => catalog.Require(id).CreateHandlers(level);
        context.AdvanceTo(TimeSpan.Zero);
        StatusAbility listener = source.EventReceiver.GetHandlers<StatusAbility>().Single();
        context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            SignalId = "pulse", CommandEventId = Guid.NewGuid(), Source = null, Target = target,
            Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(1, listener.GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(20m, source.Property.Energy);
        Assert.AreEqual(TimeSpan.Zero, listener.GateManager.NextReadyTime);
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        Assert.IsEmpty(target.EventReceiver.History.OfType<ScheduledHealTickEvent>());
        Assert.AreEqual(CommandExecutionFailureReason.PeriodicEffectLimit,
            source.EventReceiver.History.OfType<AbilityActivationResultEvent>().Single().FailureReason);
        Assert.HasCount(1, diagnostics);
        TestContext.WriteLine("Two compiled periodic effects exceeded quota 1 without consuming energy, charges or cooldown.");
    }

    [TestMethod]
    [DataRow("ExtendAndStack", 2, 3, "ability.level + 3", 10)]
    [DataRow("ExtendOnly", 1, 3, "ability.level + 3", 5)]
    [DataRow("StackOnly", 2, 2, "ability.level + 3", 10)]
    [DataRow("ExtendAndStack", 3, 3, "0.334", 0)]
    [DataRow("ExtendAndStack", 3, 3, "0.335", 1)]
    public void ReapplyingPeriodicStatusContentRetainsItsScheduleAndUsesCurrentStacks(
        string stackPolicy, int stacks, int tickCount, string formula, int expectedDamage)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("stacked-periodic-status.yaml", $$"""
                damageType: [{ id: pure }]
                ability:
                  - id: receiver
                    type: BeingDamage
                    allowMultiple: false
                    trigger: { type: None }
                  - id: periodic-status
                    type: Status
                    allowMultiple: false
                    trigger: { type: None }
                    data:
                      durationSeconds: 3
                      stackPolicy: {{stackPolicy}}
                      {{(stackPolicy == "ExtendOnly" ? "" : $"stackLimit: {stacks}")}}
                      periodicEffects:
                        - type: Damage
                          tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
                          damage: [{ type: pure, formula: "{{formula}}" }]
                        - type: Heal
                          tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
                          amount: 2
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        var factory = new TriggerChainFactory(maxActivePeriodicEffects: 2);
        using var context = new Context(causalityFactory: factory);
        context.Logger.Output = null;
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, target, catalog, "receiver", 1);
        context.AdvanceTo(TimeSpan.Zero);
        target.Property.LifePoint = 50;
        StatusApplicationOrigin origin = StatusApplicationOrigin.FromRule(target.ActionGate!, target.EventReceiver);
        void Apply() => context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = target, AbilityIds = ["periodic-status"],
            Level = 2, Purpose = AbilityInstallPurpose.StatusApplication,
            ApplicationOrigin = origin, Receiver = target.EventReceiver
        }).ToList();
        Apply();
        context.AdvanceTo(TimeSpan.FromSeconds(0.5));
        StatusAbility original = target.EventReceiver.GetHandlers<StatusAbility>().Single();
        for (int application = 1; application < Math.Max(2, stacks); application++) Apply();
        context.AdvanceTo(TimeSpan.FromSeconds(1.5));
        Apply();
        context.AdvanceTo(TimeSpan.FromSeconds(3.5));
        if (stackPolicy != "StackOnly")
        {
            Assert.AreSame(original, target.EventReceiver.GetHandlers<StatusAbility>().Single());
            Apply();
        }
        context.AdvanceTo(TimeSpan.FromSeconds(7));
        Assert.HasCount(2, target.EventReceiver.History.OfType<PeriodicEffectStartedEvent>());
        CollectionAssert.AreEqual(expectedDamage == 0 ? [] : Enumerable.Repeat(expectedDamage, tickCount).ToArray(),
            target.EventReceiver.History.OfType<DamageLifeReduceEvent>().Select(ev => ev.LifePointReduce).ToArray());
        CollectionAssert.AreEqual(Enumerable.Repeat(2 * stacks, tickCount).ToArray(),
            target.EventReceiver.History.OfType<HealLifeGainEvent>().Select(ev => ev.LifeGain).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, tickCount).Select(value => (double)value).ToArray(),
            target.EventReceiver.History.OfType<ScheduledDamageTickEvent>().Select(ev => ev.Execute!.Value.TotalSeconds).ToArray());
        Assert.AreEqual(50 + (2 * stacks - expectedDamage) * tickCount, target.Property.LifePoint);
        Assert.AreEqual(0, factory.GetActivePeriodicEffectCount(context));
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        TestContext.WriteLine($"{stackPolicy}: reapplication retained two executors, {tickCount} original tick times and {stacks} effective stacks, including at the stack limit; formula {formula} reduced {expectedDamage} life per tick.");
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("[{ type: Heal, amount: 1 }]")]
    [DataRow("[{ type: Unknown, tick: { tickCount: 1, tickIntervalSeconds: 1 } }]")]
    [DataRow("[{ type: Heal, tick: { tickCount: 0, tickIntervalSeconds: 1 }, amount: 1 }]")]
    [DataRow("[{ type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 0 }, amount: 1 }]")]
    [DataRow("[{ type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 1, firstTickDelaySeconds: -1 }, amount: 1 }]")]
    [DataRow("[{ type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 1 }, amount: -1 }]")]
    [DataRow("[{ type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 1 }, amount: 1, restoreToMaximum: true }]")]
    [DataRow("[{ type: Heal, tick: { tickCount: 1, tickIntervalSeconds: 1 }, damage: [], amount: 1 }]")]
    [DataRow("[{ type: Damage, tick: { tickCount: 1, tickIntervalSeconds: 1 }, damage: [] }]")]
    [DataRow("[{ type: Damage, tick: { tickCount: 1, tickIntervalSeconds: 1 }, damage: [{ type: missing, formula: '1' }] }]")]
    [DataRow("[{ type: Damage, tick: { tickCount: 1, tickIntervalSeconds: 1 }, damage: [{ type: pure, formula: 'missing' }] }]")]
    [DataRow("[{ type: Damage, tick: { tickCount: 1, tickIntervalSeconds: 1 }, amount: 1, damage: [{ type: pure, formula: '1' }] }]")]
    public void InvalidPeriodicStatusContentFailsAtLoad(string effects)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("invalid-periodic-status.yaml", $$"""
                    damageType: [{ id: pure }]
                    ability:
                      - id: periodic-status
                        type: Status
                        allowMultiple: false
                        trigger: { type: None }
                        data: { periodicEffects: {{effects}} }
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-periodic-status.yaml");
        TestContext.WriteLine($"Rejected periodic status: {effects}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StatusFormulaFallbackRespectsEvaluationPolicyForActorlessOrigins(bool production)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("actorless-status.yaml", """
                ability:
                  - id: status
                    type: Status
                    allowMultiple: false
                    trigger: { type: None }
                    data:
                      modifiers:
                        - { target: lifePointMax, formula: "source.level + 2", fallback: "7" }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        var diagnostics = new List<FormulaDiagnostic>();
        FormulaEvaluationPolicy policy = production
            ? new FormulaEvaluationPolicy(FormulaEvaluationMode.Production, diagnostics.Add)
            : FormulaEvaluationPolicy.Strict;
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit ruleOwner = CreateUnit(context, "rule-owner");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(ruleOwner.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        target.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory =
            (id, level) => catalog.Require(id).CreateHandlers(level, policy);
        StatusApplicationOrigin origin = StatusApplicationOrigin.FromRule(ruleOwner.ActionGate!, ruleOwner.EventReceiver);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = null, Target = target, AbilityIds = ["status"],
            Level = 2, Purpose = AbilityInstallPurpose.StatusApplication,
            ApplicationOrigin = origin, Receiver = target.EventReceiver
        }).ToList();

        if (production)
        {
            context.AdvanceTo(TimeSpan.Zero);
            Assert.AreEqual(107, target.Property.LifePointMax);
            Assert.IsTrue(target.Abilities.UsedFormulaFallback);
            Assert.HasCount(1, diagnostics);
            Assert.IsNull(target.EventReceiver.GetHandlers<StatusAbility>().Single().ApplicationSource);
            TestContext.WriteLine("Actorless status used the reported production fallback of 7.");
        }
        else
        {
            Assert.ThrowsExactly<ExpressionEvaluationException>(() => context.AdvanceTo(TimeSpan.Zero));
            Assert.AreEqual(100, target.Property.LifePointMax);
            Assert.IsEmpty(target.EventReceiver.GetHandlers<StatusAbility>());
            Assert.IsEmpty(diagnostics);
            TestContext.WriteLine("Actorless status rejected missing source.level in strict mode without installing.");
        }
    }

    [TestMethod]
    [DataRow("{ modifiers: [{ target: lifePointMax, factor: 10 }] }")]
    [DataRow("{ modifiers: [{ target: lifePointMax, factors: [10] }] }")]
    [DataRow("{ modifiers: [{ target: missing, formula: '1' }] }")]
    [DataRow("{ modifiers: [{ target: lifePointMax, formula: 'missing.value' }] }")]
    [DataRow("{ stackPolicy: ExtendOnly, stackLimit: 2 }")]
    [DataRow("{ timeBasis: RealTime }")]
    [DataRow("{ timeBasis: RealTime, durationSeconds: 2, stackPolicy: StackOnly }")]
    [DataRow("{ durationSeconds: 2, protectedUntilExpiry: true }")]
    [DataRow("{ stackPolicy: '2' }")]
    public void InvalidStatusContentFailsAtLoad(string data)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        var error = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("invalid-status.yaml", $$"""
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                  - id: status
                    type: Status
                    allowMultiple: false
                    trigger: { type: ProjectileHit, sourceAbilityIds: [delivery] }
                    data: {{data}}
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-status.yaml");
        TestContext.WriteLine($"Rejected status data: {data}");
    }

    [TestMethod]
    [DataRow(null, 1, false)]
    [DataRow("Self", 1, false)]
    [DataRow("Ally", 1, true)]
    [DataRow("Ally", 2, false)]
    [DataRow("Enemy", 1, false)]
    [DataRow("Enemy", 2, true)]
    [DataRow("Any", 1, true)]
    public void LoadedProjectileHitScopeRoutesTheOwnersOwnHealingAbility(
        string? scope, int observerTeam, bool expectedMatch)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        string scopeOption = scope == null ? "" : $", eventSourceScope: {scope}";
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("scope.yaml", $$"""
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                  - id: response
                    type: Heal
                    allowMultiple: false
                    trigger: { type: ProjectileHit, sourceAbilityIds: [delivery]{{scopeOption}} }
                    data:
                      amount: 7
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        source.TeamId = 1;
        observer.TeamId = observerTeam;
        target.TeamId = 2;
        target.Property.LifePoint = 50;
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        foreach (Unit unit in new[] { source, observer, target })
            context.AddEventReceiver(unit.EventReceiver);
        InstallCatalog(context, observer, catalog, "response", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        Assert.AreEqual(expectedMatch ? 57 : 50, target.Property.LifePoint);
        Assert.AreEqual(expectedMatch ? 15m : 20m, observer.Property.Energy);
        HealEvent[] heals = context.History.OfType<HealEvent>().Where(ev => ev.Receiver == target.EventReceiver).ToArray();
        Assert.HasCount(expectedMatch ? 1 : 0, heals);
        if (expectedMatch) Assert.AreSame(observer, heals[0].Source);
        TestContext.WriteLine($"YAML scope {scope ?? "(default)"}, observer team {observerTeam}: matched={expectedMatch}.");
    }

    [TestMethod]
    [DataRow("Self", 1, false)]
    [DataRow("Ally", 1, true)]
    [DataRow("Enemy", 2, true)]
    [DataRow("Any", 1, true)]
    public void LoadedCommandObserverEmitsFromItsOwner(string scope, int observerTeam, bool expectedMatch)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("command-scope.yaml", $$"""
                ability:
                  - id: fire
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                  - id: response
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command, sourceAbilityIds: [fire], eventSourceScope: {{scope}} }
                    data:
                      speed: 0
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        source.TeamId = 1;
        observer.TeamId = observerTeam;
        target.TeamId = 2;
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        foreach (Unit unit in new[] { source, observer, target })
            context.AddEventReceiver(unit.EventReceiver);
        var map = new MapRuntime();
        Place(map, source, new Vector2D(0, 0));
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, new Vector2D(2, 0));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["1"], [new MapVisibilityObserver("1", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, source, catalog, "fire", 1);
        InstallCatalog(context, observer, catalog, "response", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        CommandEvent command = context.Factory.NewEvent<CommandEvent>() with
        {
            AbilityId = "fire", Source = source, Target = target, Receiver = source.EventReceiver
        };
        context.Dispatch(command).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        CommandExecutionEvent commandResult = source.EventReceiver.History.OfType<CommandExecutionEvent>()
            .Single(result => ReferenceEquals(result.Command, command));
        Assert.AreEqual(CommandExecutionStatus.Success, commandResult.Status, $"Command failed: {commandResult.FailureReason}");
        ProjectileEvent[] emissions = observer.EventReceiver.History.OfType<ProjectileEvent>()
            .Where(ev => ev.AbilityId == "response").ToArray();
        Assert.HasCount(expectedMatch ? 1 : 0, emissions);
        Assert.AreEqual(expectedMatch ? 15m : 20m, observer.Property.Energy);
        if (expectedMatch)
        {
            Assert.AreSame(observer, emissions[0].Source);
            Assert.AreSame(target, emissions[0].Target);
        }
        ProjectileHitEvent[] hits = observer.EventReceiver.History.OfType<ProjectileHitEvent>()
            .Where(ev => ev.AbilityId == "response").ToArray();
        Assert.HasCount(expectedMatch ? 1 : 0, hits);
        TestContext.WriteLine($"YAML command scope {scope}, observer team {observerTeam}: matched={expectedMatch}.");
    }

    [TestMethod]
    public void LoadedExplosionObserverEmitsFromItsOwnerAtTheOriginalPoint()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("explosion-scope.yaml", """
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                    data:
                      explosionTrigger: OnHit
                      gate:
                        windupSeconds: 0
                        casting: { canMove: false, canInterrupt: true, interruptOnDamage: true }
                  - id: response
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: ProjectileExplosion, sourceAbilityIds: [delivery], eventSourceScope: Any }
                    data:
                      speed: 0
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(observer.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        var point = new Vector2D(3, 4);
        var map = new MapRuntime();
        Place(map, source, new Vector2D(0, 0));
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, point);
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"],
                [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, source, catalog, "delivery", 1);
        InstallCatalog(context, observer, catalog, "response", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        ProjectileAbility delivery = source.EventReceiver.GetHandlers<ProjectileAbility>().Single();
        Assert.AreEqual(ProjectileExplosionTrigger.OnHit, delivery.ExplosionTrigger);
        Assert.IsNotNull(delivery.Casting);
        Assert.IsFalse(delivery.Casting.CanMove);
        Assert.IsTrue(delivery.Casting.CanInterrupt);
        Assert.IsTrue(delivery.Casting.InterruptOnDamage);

        context.Dispatch(context.Factory.NewEvent<CommandEvent>() with
        {
            AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver
        }).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        Assert.HasCount(1, source.EventReceiver.History.OfType<ProjectileExplosionEvent>());
        Assert.HasCount(1, source.EventReceiver.History.OfType<ProjectileHitEvent>()
            .Where(ev => ev.GetType() == typeof(ProjectileHitEvent)));
        ProjectileEvent emission = observer.EventReceiver.History.OfType<ProjectileEvent>()
            .Single(ev => ev.AbilityId == "response");
        Assert.AreSame(observer, emission.Source);
        Assert.AreEqual(point, emission.TargetPoint);
        Assert.AreEqual(15m, observer.Property.Energy);
        Assert.HasCount(1, observer.EventReceiver.History.OfType<ProjectileEndedEvent>());
        TestContext.WriteLine("A real hit produced one explosion; the YAML listener spent its own resources and emitted at that point.");
    }

    [TestMethod]
    [DataRow("1", "casting: { canMove: true }", "canInterrupt")]
    [DataRow("1", "casting: { canMove: true, canInterrupt: true, durationSeconds: 2 }", "matching windupSeconds")]
    public void ProjectileCastingRequiresCompleteConsistentConfiguration(
        string windup, string casting, string expected)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        string yaml = $$"""
            ability:
              - id: delivery
                type: Projectile
                allowMultiple: false
                trigger: { type: Command }
                data:
                  gate:
                    windupSeconds: {{windup}}
                    {{casting}}
            """;
        Exception error = Assert.Throws<Exception>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load(
                [Doc("invalid-casting.yaml", yaml)],
                [SkillContentServiceCollectionExtensions.AbilityModuleId,
                    SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, expected);
    }

    [TestMethod]
    public void LoadedLifeReductionObserverRespondsUsingItsOwnResources()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("life-scope.yaml", """
                damageType: [{ id: pure }]
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: { type: ProjectileHit }
                    data: { damage: [{ type: pure, formula: "10" }] }
                  - id: receiver
                    type: BeingDamage
                    allowMultiple: false
                    trigger: { type: None }
                  - id: response
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: TargetLifeReduce
                      sourceAbilityIds: [payload]
                      eventSourceScope: Any
                      conditions:
                        - { type: Expression, expression: 'event.source != holder && event.target != holder && event.ability.definition == "payload"' }
                        - { type: Distance, anchor: Target, maximum: 1 }
                    data:
                      speed: 0
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        foreach (Unit unit in new[] { source, observer, target })
            context.AddEventReceiver(unit.EventReceiver);
        var map = new MapRuntime();
        Place(map, source, new Vector2D(0, 0));
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, new Vector2D(2, 0));
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, target, catalog, "receiver", 1);
        InstallCatalog(context, observer, catalog, "response", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        DamageAbility payload = catalog.Require("payload").CreateHandlers().OfType<DamageAbility>().Single();
        DamageEvent damage = context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = target, Receiver = source.EventReceiver,
            SourceObject = source.MapObject, TargetObject = target.MapObject
        };
        damage.AppendDamage(DamageProfile.From(payload).Calculate(source, target, TimeSpan.Zero));

        context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        Assert.AreEqual(90, target.Property.LifePoint);
        Assert.AreEqual(15m, observer.Property.Energy);
        Assert.AreEqual(0, observer.EventReceiver.GetHandlers<ProjectileAbility>()
            .Single().GateManager!.GetChargeSnapshot()!.Available);
        ProjectileEvent emission = observer.EventReceiver.History.OfType<ProjectileEvent>().Single();
        Assert.AreSame(observer, emission.Source);
        Assert.AreSame(observer.MapObject, emission.SourceObject);
        Assert.AreSame(target, emission.Target);
        Assert.HasCount(1, target.EventReceiver.History.OfType<ProjectileHitEvent>());
        TestContext.WriteLine("A YAML Any-scope life-reduction listener emitted from its owner after real damage and spent that owner's resources.");
    }

    [TestMethod]
    public void LoadedDeathPhasesRespondSeparatelyToOneLethalDamage()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("death-phases.yaml", """
                damageType: [{ id: pure }]
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: { type: ProjectileHit }
                    data: { damage: [{ type: pure, formula: "210" }] }
                  - id: receiver
                    type: BeingDamage
                    allowMultiple: false
                    trigger: { type: None }
                  - id: pending
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: Death
                      deathPhase: Pending
                      sourceAbilityIds: [payload]
                      eventSourceScope: Any
                      conditions:
                        - { type: Expression, expression: 'event.source != null && event.target != holder && event.ability.definition == "payload"' }
                        - { type: Distance, anchor: Source, maximum: 1 }
                    data:
                      speed: 0
                      selector: { type: tracking, origin: EventSource }
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                  - id: confirmed
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: Death
                      sourceAbilityIds: [payload]
                      eventSourceScope: Any
                      conditions:
                        - { type: Expression, expression: 'event.source != null && event.target != holder && event.ability.definition == "payload"' }
                        - { type: Distance, anchor: Target, maximum: 1 }
                    data:
                      speed: 0
                      selector: { type: tracking, origin: Source }
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        var map = new MapRuntime();
        var registry = new UnitSpatialRegistry();
        foreach ((Unit unit, Vector2D position) in new[]
        {
            (source, new Vector2D(0, 0)), (observer, new Vector2D(1, 0)), (target, new Vector2D(2, 0))
        })
        {
            context.AddEventReceiver(unit.EventReceiver);
            Place(map, unit, position);
            registry.Register(unit);
            unit.Registry = registry;
        }
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        target.EventReceiver.AddHandler(new DeathAbility { Id = "death" });
        InstallCatalog(context, target, catalog, "receiver", 1);
        InstallCatalog(context, observer, catalog, "pending", 1);
        InstallCatalog(context, observer, catalog, "confirmed", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        DamageAbility payload = catalog.Require("payload").CreateHandlers().OfType<DamageAbility>().Single();
        DamageEvent damage = context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = source, Target = target, Receiver = source.EventReceiver,
            SourceObject = source.MapObject, TargetObject = target.MapObject
        };
        damage.AppendDamage(DamageProfile.From(payload).Calculate(source, target, TimeSpan.Zero));

        context.Dispatch(DamageRequestEvent.FromDamage(damage)).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        Assert.IsFalse(target.IsAlive);
        Assert.AreEqual(target.CurrentDeathId,
            target.EventReceiver.History.OfType<DeathResolvedEvent>().Single().DeathId);
        ProjectileEvent[] emissions = observer.EventReceiver.History.OfType<ProjectileEvent>().ToArray();
        CollectionAssert.AreEquivalent(new[] { "pending", "confirmed" }, emissions.Select(ev => ev.AbilityId).ToArray());
        Assert.IsTrue(emissions.All(ev => ReferenceEquals(ev.Source, observer)));
        Assert.AreSame(source, emissions.Single(ev => ev.AbilityId == "pending").Target);
        Assert.AreSame(observer, emissions.Single(ev => ev.AbilityId == "confirmed").Target);
        Assert.AreSame(source.MapObject, emissions.Single(ev => ev.AbilityId == "pending").TargetObject);
        Assert.AreEqual(10m, observer.Property.Energy);
        Assert.IsTrue(observer.EventReceiver.GetHandlers<ProjectileAbility>()
            .All(ability => ability.GateManager!.GetChargeSnapshot()!.Available == 0));
        TestContext.WriteLine("YAML Pending selected the event killer and Confirmed selected the holder, with one response per phase.");
    }

    [TestMethod]
    public void LoadedSignalConditionsUseEventRolesAndTheRespondersResources()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("signal-observation.yaml", """
                ability:
                  - id: response
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: Signal
                      signalId: pulse
                      eventSourceScope: Any
                      conditions:
                        - { type: Expression, expression: 'event.source == null && event.ability == null && event.target != holder && listener.ability.definition == "response"' }
                        - { type: Distance, anchor: Target, maximum: 1 }
                    data:
                      speed: 0
                      selector: { type: tracking, origin: EventTarget }
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                  - id: missing-source
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: Signal
                      signalId: pulse
                      eventSourceScope: Any
                    data:
                      speed: 0
                      selector: { type: tracking, origin: EventSource }
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        context.AddEventReceiver(observer.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        var map = new MapRuntime();
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, new Vector2D(2, 0));
        var registry = new UnitSpatialRegistry();
        registry.Register(observer);
        registry.Register(target);
        observer.Registry = registry;
        target.Registry = registry;
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, observer, catalog, "response", 1);
        InstallCatalog(context, observer, catalog, "missing-source", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            SignalId = "pulse", CommandEventId = Guid.NewGuid(), Source = null, Target = target,
            Receiver = target.EventReceiver
        }).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        Assert.AreEqual(15m, observer.Property.Energy);
        ProjectileEvent emission = observer.EventReceiver.History.OfType<ProjectileEvent>().Single();
        Assert.AreSame(observer, emission.Source);
        Assert.AreSame(observer.MapObject, emission.SourceObject);
        Assert.AreSame(target, emission.Target);
        ProjectileAbility[] abilities = observer.EventReceiver.GetHandlers<ProjectileAbility>().ToArray();
        Assert.AreEqual(0, abilities.Single(ability => ability.Id == "response")
            .GateManager!.GetChargeSnapshot()!.Available);
        Assert.AreEqual(1, abilities.Single(ability => ability.Id == "missing-source")
            .GateManager!.GetChargeSnapshot()!.Available);

        Unit remote = CreateUnit(context, "remote");
        context.AddEventReceiver(remote.EventReceiver);
        Place(new MapRuntime(), remote, new Vector2D(1, 0));
        registry.Register(remote);
        remote.Registry = registry;
        context.Dispatch(context.Factory.NewEvent<AbilitySignalEvent>() with
        {
            SignalId = "pulse", CommandEventId = Guid.NewGuid(), Source = remote, Target = target,
            Receiver = remote.EventReceiver
        }).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        Assert.AreEqual(15m, observer.Property.Energy);
        Assert.HasCount(1, observer.EventReceiver.History.OfType<ProjectileEvent>());
        Assert.AreEqual(1, abilities.Single(ability => ability.Id == "missing-source")
            .GateManager!.GetChargeSnapshot()!.Available);
        TestContext.WriteLine("An actorless signal selected its real target; null and different-map sources consumed no charge.");
    }

    [TestMethod]
    [DataRow("Signal", "")]
    [DataRow("Signal", ", signalId: ' '")]
    [DataRow("Command", ", signalId: pulse")]
    public void SignalIdentifiersAreRequiredOnlyForSignalTriggers(string type, string option)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("invalid-signal.yaml", $$"""
                    ability:
                      - id: response
                        type: Projectile
                        allowMultiple: false
                        trigger: { type: {{type}}{{option}} }
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-signal.yaml");
        StringAssert.Contains(error.Message, "signalId");
        TestContext.WriteLine($"Rejected invalid signal identifier configuration for {type}.");
    }

    [TestMethod]
    public void LoadedHealObserverRespondsOnceUsingItsOwnResources()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = provider.GetRequiredService<IContentCatalogLoader>().Load([
            Doc("heal-observation.yaml", """
                ability:
                  - id: healing
                    type: Heal
                    allowMultiple: false
                    trigger: { type: ProjectileHit }
                    data: { amount: 10 }
                  - id: response
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: Heal
                      sourceAbilityIds: [healing]
                      eventSourceScope: Any
                      conditions:
                        - { type: Expression, expression: 'event.source != holder && event.target != holder && event.ability.definition == "healing"' }
                        - { type: Distance, anchor: Target, maximum: 1 }
                    data:
                      speed: 0
                      gate: { resourceCost: 5, occupiesWindup: false, charges: { maximum: 1, initial: 1 } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId])
            .GetSection<AbilityCatalog>("ability");
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit observer = CreateUnit(context, "observer");
        Unit target = CreateUnit(context, "target");
        observer.Property.EnergyMax = 20;
        observer.Property.Energy = 20;
        target.Property.LifePoint = 95;
        foreach (Unit unit in new[] { source, observer, target }) context.AddEventReceiver(unit.EventReceiver);
        var map = new MapRuntime();
        Place(map, source, new Vector2D(0, 0));
        Place(map, observer, new Vector2D(1, 0));
        Place(map, target, new Vector2D(2, 0));
        observer.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        InstallCatalog(context, source, catalog, "healing", 1);
        InstallCatalog(context, observer, catalog, "response", 1);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "initial-delivery",
            Source = source, Target = target, SourceObject = source.MapObject, TargetObject = target.MapObject,
            Receiver = source.EventReceiver
        })).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);

        Assert.AreEqual(100, target.Property.LifePoint);
        Assert.AreEqual(5, target.EventReceiver.History.OfType<HealLifeGainEvent>().Single().LifeGain);
        Assert.AreEqual(10, context.History.OfType<HealPublicationEvent>().Single().Heal.HealAmount);
        Assert.AreEqual(15m, observer.Property.Energy);
        ProjectileEvent emission = observer.EventReceiver.History.OfType<ProjectileEvent>().Single();
        Assert.AreSame(observer, emission.Source);
        Assert.AreSame(observer.MapObject, emission.SourceObject);
        Assert.AreSame(target, emission.Target);
        TestContext.WriteLine("A YAML heal-request listener emitted once from the observer, spent its resources, and distinguished request 10 from actual gain 5.");
    }

    [TestMethod]
    [DataRow("Death", "Unknown")]
    [DataRow("Death", "0")]
    [DataRow("ProjectileHit", "Pending")]
    public void InvalidDeathPhasesAreRejectedAtContentLoad(string trigger, string phase)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("death-phase-error.yaml", $$"""
                    ability:
                      - id: response
                        type: Projectile
                        allowMultiple: false
                        trigger: { type: {{trigger}}, deathPhase: {{phase}} }
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "death-phase-error.yaml");
        StringAssert.Contains(error.Message, "deathPhase");
        TestContext.WriteLine($"Rejected death phase '{phase}' for {trigger}.");
    }

    [TestMethod]
    [DataRow("Maybe", "ProjectileHit")]
    [DataRow("1", "ProjectileHit")]
    [DataRow("Any", "None")]
    public void UnsupportedEventSourceScopesAreRejectedAtContentLoad(string scope, string trigger)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("scope-error.yaml", $$"""
                    ability:
                      - id: response
                        type: Projectile
                        allowMultiple: false
                        trigger: { type: {{trigger}}, eventSourceScope: {{scope}} }
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "scope-error.yaml");
        StringAssert.Contains(error.Message, "eventSourceScope");
        TestContext.WriteLine($"Rejected scope '{scope}' for {trigger} explicitly at load time.");
    }

    [TestMethod]
    [DataRow("type: line, range: 5")]
    [DataRow("type: circle, radius: -1")]
    [DataRow("type: tracking, radius: 2")]
    [DataRow("type: tracking, filter: unknown")]
    [DataRow("type: circle, radius: 1, origin: EventSource")]
    [DataRow("type: line, range: 5, width: 1, origin: EventTarget")]
    [DataRow("type: unknown")]
    public void InvalidAbilitySelectorsFailAtCatalogLoad(string selector)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        var error = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("invalid-selector.yaml", $$"""
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: { type: Command }
                    data: { selector: { {{selector}} } }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-selector.yaml");
        TestContext.WriteLine($"Rejected selector: {selector}");
    }

    [TestMethod]
    [DataRow("Death", "")]
    [DataRow("Signal", ", signalId: pulse")]
    public void LegacyTriggerSelectorIsRejectedRatherThanIgnored(string type, string options)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            provider.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("legacy-trigger-selector.yaml", $$"""
                    ability:
                      - id: response
                        type: Projectile
                        allowMultiple: false
                        trigger: { type: {{type}}{{options}}, selector: { type: tracking } }
                    """)
            ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "legacy-trigger-selector.yaml");
        TestContext.WriteLine($"A legacy {type} trigger selector was rejected instead of being ignored.");
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{ amount: -1 }")]
    [DataRow("{ amount: 7, restoreToMaximum: true }")]
    public void InvalidHealPayloadsFailAtCatalogLoad(string data)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        var error = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("invalid-heal.yaml", $$"""
                ability:
                  - id: heal
                    type: Heal
                    allowMultiple: false
                    trigger: { type: ProjectileHit }
                    data: {{data}}
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, SkillContentServiceCollectionExtensions.DamageModuleId]));
        StringAssert.Contains(error.Message, "invalid-heal.yaml");
        TestContext.WriteLine($"Rejected heal payload: {data}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SelectedModuleSuppliesItsParserThroughDiWithoutCreatingHandlersAtLoad(bool changesType)
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent();
        services.AddAbilityContent();
        var parser = new MarkerParser { CompiledType = changesType ? "Other" : "Marker" };
        services.AddSingleton(parser);
        services.AddSingleton<IContentModule, MarkerModule>();
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        ContentDocument document = Doc("marker.yaml", """
            ability:
              - id: marker
                type: Marker
                allowMultiple: false
                trigger: { type: None }
                data: { value: 3 }
            """);
        string[] modules = [SkillContentServiceCollectionExtensions.AbilityModuleId,
            SkillContentServiceCollectionExtensions.DamageModuleId];
        var unloaded = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([document], modules));
        StringAssert.Contains(unloaded.Message, "marker.yaml");
        StringAssert.Contains(unloaded.Message, "Marker");

        if (changesType)
        {
            var mismatch = Assert.ThrowsExactly<InvalidDataException>(() =>
                loader.Load([document], [.. modules, "marker-test"]));
            StringAssert.Contains(mismatch.Message, "marker.yaml");
            StringAssert.Contains(mismatch.Message, "Compiled type 'Other'");
            Assert.AreEqual(0, parser.Created);
            TestContext.WriteLine("The selected module's compiler changed the declared type; loading rejected it without creating handlers.");
            return;
        }
        AbilityCatalog catalog = loader.Load([document], [.. modules, "marker-test"])
            .GetSection<AbilityCatalog>("ability");
        Assert.AreEqual(0, parser.Created);
        var first = (MarkerAbility)catalog.Require("marker").CreateHandlers(2).Single();
        var second = (MarkerAbility)catalog.Require("marker").CreateHandlers(2).Single();
        Assert.AreEqual(2, parser.Created);
        Assert.AreEqual(5, first.Value);
        Assert.AreNotSame(first, second);
        TestContext.WriteLine("The selected DI module supplied a parser; compilation created no handlers and each installation created a fresh one.");
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent().AddDamageTypeContent();
        services.AddSingleton<IContentModule>(new ArmorFieldModule("armor-half", 2));
        services.AddSingleton<IContentModule>(new ArmorFieldModule("armor-quarter", 4));
        return services.BuildServiceProvider(validateScopes: true);
    }

    [TestMethod]
    [DataRow("event.source == holder && event.target != holder", true)]
    [DataRow("event.ability != listener.ability && event.ability.definition == \"delivery\"", true)]
    [DataRow("event.ability.source.category == \"Equipment\" && listener.ability.source.category == \"Talent\"", true)]
    [DataRow("event.ability.source == listener.ability.source", false)]
    [DataRow("event.ability.source != listener.ability.source", true)]
    public void LoadedConditionsUseActualAbilityAndSourceIdentities(string expression, bool matches)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageTrigger: $"{{ type: ProjectileHit, conditions: [{{ type: Expression, expression: '{expression}' }}] }}");
        Assert.AreEqual(matches ? 10 : 0, RunConditionChain(catalog));
        TestContext.WriteLine($"Actual command/projectile chain: {expression} => {matches}; response used its own bound Talent source.");
    }

    [TestMethod]
    [DataRow("event.ability.source.category != \"Equipment\"", false)]
    [DataRow("event.ability.source == null || event.ability.source.category != \"Equipment\"", true)]
    [DataRow("event.ability.source == listener.ability.source", false)]
    public void LoadedConditionsKeepMissingAbilitySourcesUnknown(string expression, bool matches)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageTrigger: $"{{ type: ProjectileHit, conditions: [{{ type: Expression, expression: '{expression}' }}] }}");
        Assert.AreEqual(matches ? 10 : 0, RunConditionChain(catalog, bindSources: false));
        TestContext.WriteLine($"Source-less abilities: {expression} => {matches}; two missing sources were not equal.");
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 10)]
    public void SourceIdentityComparisonDistinguishesSameCategoryInstances(bool sharedSource, int expectedDamage)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageTrigger: """
                { type: ProjectileHit, conditions: [
                  { type: Expression, expression: 'event.ability.source.category == listener.ability.source.category' },
                  { type: Expression, expression: 'event.ability.source == listener.ability.source' }] }
                """);
        Assert.AreEqual(expectedDamage, RunConditionChain(catalog, sameCategory: true, sharedSource: sharedSource));
        TestContext.WriteLine($"Two Equipment grants with sharedSource={sharedSource}: identity condition produced {expectedDamage} damage.");
    }

    [TestMethod]
    public void CommandConditionsDoNotInventAnEventProducingAbility()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            projectileTrigger: """
                { type: Command, conditions: [{ type: Expression, expression: 'event.ability == null && event.ability.definition == null && event.ability.source == null && listener.ability.definition == "delivery"' }] }
                """);
        Assert.AreEqual(10, RunConditionChain(catalog));
        TestContext.WriteLine("A requested command ability was not mistaken for an event-producing ability.");
    }

    [TestMethod]
    public void LoadedPresetConditionsCombineWithExpressionsWithoutChangingSourceScope()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog matches = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageTrigger: """
                { type: ProjectileHit, conditions: [
                  { type: Expression, expression: 'event.source == holder' },
                  { type: TargetRelation, relation: Ally },
                  { type: Distance, anchor: Source, maximum: 0 },
                  { type: Distance, anchor: Target, maximum: 10 },
                  { type: Distance, anchor: HitPoint, maximum: 10 }] }
                """);
        Assert.AreEqual(10, RunConditionChain(matches));
        AbilityCatalog rejected = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageTrigger: """
                { type: ProjectileHit, conditions: [
                  { type: TargetRelation, relation: Self },
                  { type: Expression, expression: '1 / 0 > 0' }] }
                """);
        Assert.AreEqual(0, RunConditionChain(rejected));
        TestContext.WriteLine("YAML mixed expressions and all distance anchors; target Self rejected an outgoing hit before a failing expression.");
    }

    [TestMethod]
    [DataRow("{ type: Expression, expression: 'holder.life > 0' }")]
    [DataRow("{ type: Expression, expression: 'holder == event.ability' }")]
    [DataRow("{ type: Expression, expression: 'listener.ability.source == \"a-guid\"' }")]
    [DataRow("{ type: Expression, expression: '1' }")]
    [DataRow("{ type: Expression, expression: 'true', relation: Self }")]
    [DataRow("{ type: TargetRelation, relation: self }")]
    [DataRow("{ type: Distance, anchor: Target, maximum: -1 }")]
    [DataRow("{ type: Distance, anchor: 0, maximum: 10 }")]
    [DataRow("null")]
    public void InvalidTriggerConditionsFailAtLoadWithSourceDiagnostics(string condition)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
                damageTrigger: $"{{ type: ProjectileHit, conditions: [{condition}] }}"));
        StringAssert.Contains(error.Message, "payload.yaml");
        StringAssert.Contains(error.Message, "payload");
        TestContext.WriteLine($"Rejected condition at load: {condition}");
    }

    [TestMethod]
    public void ConditionsRejectInapplicableDistanceAnchorsAndNoneOptions()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        foreach (string trigger in new[]
        {
            "{ type: Command, conditions: [{ type: Distance, anchor: HitPoint, maximum: 10 }] }",
            "{ type: None, conditions: [] }"
        })
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() =>
                LoadAbilities(loader, "armor-half", projectileTrigger: trigger));
            StringAssert.Contains(error.Message, "delivery");
        }
        TestContext.WriteLine("Command hit-point distance and conditions on None were rejected before runtime.");
    }

    [TestMethod]
    public void ConditionExtensionsAreCatalogLocalSealedAndEventSpecific()
    {
        var services = new ServiceCollection();
        services.AddContentLoading().AddDamageTypeContent().AddAbilityContent();
        services.AddSingleton<IContentModule>(new ArmorFieldModule("armor-half", 2));
        var allow = new ConditionFieldModule("allow-condition", _ => 1);
        var deny = new ConditionFieldModule("deny-condition", _ => 0);
        services.AddSingleton<IContentModule>(allow);
        services.AddSingleton<IContentModule>(deny);
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        const string trigger = "{ type: ProjectileHit, conditions: [{ type: Expression, expression: 'module.value > 0' }] }";
        AbilityCatalog first = LoadAbilities(loader, "armor-half", damageTrigger: trigger, conditionModules: ["allow-condition"]);
        AbilityCatalog second = LoadAbilities(loader, "armor-half", damageTrigger: trigger, conditionModules: ["deny-condition"]);
        Assert.AreEqual(10, RunConditionChain(first));
        Assert.AreEqual(0, RunConditionChain(second));
        Assert.AreEqual(10, RunConditionChain(first));
        Assert.ThrowsExactly<InvalidOperationException>(() => allow.Schema!.RegisterNumber("late", _ => 1));
        Assert.ThrowsExactly<InvalidDataException>(() => LoadAbilities(loader, "armor-half", damageTrigger: trigger));
        Assert.ThrowsExactly<InvalidDataException>(() => LoadAbilities(loader, "armor-half",
            projectileTrigger: "{ type: Command, conditions: [{ type: Expression, expression: 'module.value > 0' }] }",
            conditionModules: ["allow-condition"]));
        Assert.ThrowsExactly<InvalidOperationException>(() => LoadAbilities(loader, "armor-half",
            damageTrigger: trigger, conditionModules: ["allow-condition", "deny-condition"]));
        TestContext.WriteLine("Event-specific extension schemas produced 10, 0, 10 damage; unloaded, conflicting and late fields were rejected.");
    }

    [TestMethod]
    public void LoadedConditionArrayPreservesUnknownErrorsAndFalseShortCircuit()
    {
        using ServiceProvider provider = CreateAbilityProvider();
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        const string unknown = "{ type: Expression, expression: 'event.ability.source.category != \"Equipment\"' }";
        const string error = "{ type: Expression, expression: '1 / 0 > 0' }";
        AbilityCatalog fails = LoadAbilities(loader, "armor-half",
            damageTrigger: $"{{ type: ProjectileHit, conditions: [{unknown}, {error}] }}");
        Assert.ThrowsExactly<ExpressionEvaluationException>(() => RunConditionChain(fails, bindSources: false));
        AbilityCatalog skips = LoadAbilities(loader, "armor-half",
            damageTrigger: $"{{ type: ProjectileHit, conditions: [{unknown}, {{ type: Expression, expression: 'false' }}, {error}] }}");
        Assert.AreEqual(0, RunConditionChain(skips, bindSources: false));
        TestContext.WriteLine("YAML unknown continued into a division error; an intervening false safely skipped it.");
    }

    private static int RunConditionChain(
        AbilityCatalog catalog, bool bindSources = true, bool sameCategory = false, bool sharedSource = false)
    {
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"], [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        InstallCatalog(context, source, catalog, "delivery", 2);
        InstallCatalog(context, source, catalog, "payload", 2);
        InstallCatalog(context, target, catalog, "receiver", 2);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        if (bindSources)
        {
            var transaction = new AtomicTransaction();
            var shared = new TestAbilitySource(Guid.NewGuid(), "Equipment");
            foreach (AbilityBase ability in source.EventReceiver.GetHandlers<AbilityBase>())
            {
                ability.Binding.StageBind(transaction, sharedSource ? shared : new TestAbilitySource(Guid.NewGuid(),
                    sameCategory || ability.Id == "delivery" ? "Equipment" : "Talent"));
                ability.Binding.StageAuthority(transaction, context, source.Id);
            }
            transaction.Commit();
        }
        context.Dispatch(context.Factory.NewEvent<CommandEvent>() with
        { AbilityId = "delivery", Source = source, Target = target, Receiver = source.EventReceiver }).ToList();
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        return 100 - target.Property.LifePoint!.Value;
    }

    private sealed class ConditionFieldModule(string id, Func<TriggerConditionContext, double?> read) : IContentModule
    {
        public string Id => id;
        public ExpressionSchema<TriggerConditionContext>? Schema { get; private set; }
        public void Register(ContentSectionRegistry registry)
        {
            Schema = registry.TriggerConditions().For(TriggerType.ProjectileHit);
            Schema.RegisterNumber("module.value", read);
        }
    }

    private sealed class StatusPropertyModule(string id, double bonus) : IContentModule
    {
        public static PropertyModifierTarget Target { get; } = new("module.reserve",
            value => value >= int.MinValue && value <= int.MaxValue,
            (context, value) => context.TargetProperty.EnergyMax =
                checked(context.TargetProperty.EnergyMax + (int)value));
        public string Id => id;
        public ExpressionSchema<PropertyFormulaContext>? Formulas { get; private set; }
        public PropertyTargetRegistration? Targets { get; private set; }
        public void Register(ContentSectionRegistry registry)
        {
            Formulas = registry.PropertyFormulas();
            Targets = registry.PropertyTargets();
            Formulas.RegisterNumber("module.bonus", _ => bonus);
            Targets.Register(Target);
        }
    }

    private static DamageTypeRegistry Load(IContentCatalogLoader loader, string yaml, params string[] otherModules) =>
        loader.Load([Doc("types.yaml", yaml)],
            [SkillContentServiceCollectionExtensions.DamageModuleId, .. otherModules])
            .GetSection<DamageTypeRegistry>("damageType");

    private static ContentDocument Doc(string source, string yaml) =>
        new(source, "schemaVersion: 1\n" + yaml);

    private static ServiceProvider CreateAbilityProvider(params IContentModule[] modules)
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent();
        services.AddAbilityContent().AddAbilityContent();
        services.AddSingleton<IContentModule>(new ArmorFieldModule("armor-half", 2));
        services.AddSingleton<IContentModule>(new ArmorFieldModule("armor-quarter", 4));
        foreach (IContentModule module in modules) services.AddSingleton(module);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static AbilityCatalog LoadAbilities(
        IContentCatalogLoader loader, string armorModule, double projectileSpeed = 0, string? gate = null,
        string? damageGate = null, string? projectileTrigger = null, string? damageTick = null,
        string? damageSelector = null, string? damageTrigger = null, string[]? conditionModules = null) =>
        loader.Load([
            Doc("payload.yaml", $$"""
                ability:
                  - id: payload
                    type: Damage
                    allowMultiple: false
                    trigger: {{(damageTrigger ?? "{ type: ProjectileHit, sourceAbilityIds: [delivery] }").Replace("\n", "\n    ")}}
                    data: { damage: [{ type: impact, formula: "ability.level * 10" }]{{(damageGate == null ? "" : ", gate: " + damageGate)}}{{(damageTick == null ? "" : ", tick: " + damageTick)}}{{(damageSelector == null ? "" : ", selector: " + damageSelector)}} }
                  - id: receiver
                    type: BeingDamage
                    allowMultiple: false
                    trigger: { type: None }
                    data: { mitigationTypes: [impact] }
                """),
            Doc("types.yaml", """
                damageType:
                  - id: impact
                    mitigation: { formula: "raw / armor" }
                """),
            Doc("delivery.yaml", $$"""
                ability:
                  - id: delivery
                    type: Projectile
                    allowMultiple: false
                    trigger: {{(projectileTrigger ?? "{ type: Command }").Replace("\n", "\n    ")}}
                    data: { speed: {{projectileSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}}{{(gate == null ? "" : ", gate: " + gate)}} }
                """)
        ], [SkillContentServiceCollectionExtensions.AbilityModuleId, armorModule,
            SkillContentServiceCollectionExtensions.DamageModuleId, .. conditionModules ?? []]).GetSection<AbilityCatalog>("ability");

    private static (Context Context, TriggerChainFactory Factory, Unit Source, Unit First, Unit Second,
        Unit Outside, DamageAbility Ability) CreatePeriodicSelectorBattle(
        bool gated, int capacity, List<TriggerLimitDiagnostic> diagnostics, int windupSeconds = 0)
    {
        using ServiceProvider provider = CreateAbilityProvider();
        AbilityCatalog catalog = LoadAbilities(provider.GetRequiredService<IContentCatalogLoader>(), "armor-half",
            damageGate: gated ? $"{{ resourceCost: 5, windupSeconds: {windupSeconds}, occupiesWindup: false, charges: {{ maximum: 1, initial: 1 }} }}" : null,
            damageTick: "{ tickCount: 2, tickIntervalSeconds: 1, firstTickDelaySeconds: 0 }",
            damageSelector: "{ type: circle, origin: HitPoint, radius: 2 }");
        var factory = new TriggerChainFactory(mode: TriggerLimitMode.Production,
            report: diagnostics.Add, maxActivePeriodicEffects: capacity);
        var context = new Context(causalityFactory: factory);
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit first = CreateUnit(context, "first");
        Unit second = CreateUnit(context, "second");
        Unit outside = CreateUnit(context, "outside");
        var registry = new UnitSpatialRegistry();
        var map = new MapRuntime();
        foreach ((Unit unit, Vector2D position) in new[]
        {
            (source, new Vector2D(0, 0)), (first, new Vector2D(10, 10)),
            (second, new Vector2D(11, 10)), (outside, new Vector2D(20, 10))
        })
        {
            context.AddEventReceiver(unit.EventReceiver);
            Place(map, unit, position);
            registry.Register(unit);
            unit.Registry = registry;
        }
        InstallCatalog(context, source, catalog, "payload", 2);
        foreach (Unit target in new[] { first, second, outside })
            InstallCatalog(context, target, catalog, "receiver", 2);
        context.AdvanceTo(TimeSpan.Zero);
        source.Property.EnergyMax = 20;
        source.Property.Energy = 20;
        source.Property.EnergyRecoveryPerSecond = 0;
        return (context, factory, source, first, second, outside,
            source.EventReceiver.GetHandlers<DamageAbility>().Single());
    }

    private static void DispatchPeriodicHit(
        (Context Context, TriggerChainFactory Factory, Unit Source, Unit First, Unit Second,
            Unit Outside, DamageAbility Ability) battle, Vector2D hitPoint,
        CancellationTokenSource? cancellation = null)
    {
        battle.Context.Dispatch(ProjectileHitPublicationEvent.FromHit(battle.Context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = battle.Source,
            Target = battle.First, SourceObject = battle.Source.MapObject, TargetObject = battle.First.MapObject,
            HitPoint = hitPoint, Receiver = battle.Source.EventReceiver, CancellationTokenSource = cancellation
        })).ToList();
    }

    private static int HitCatalog(AbilityCatalog catalog, int? level)
    {
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, "source");
        Unit target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        InstallCatalog(context, source, catalog, "payload", level);
        InstallCatalog(context, target, catalog, "receiver", level);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(), AbilityId = "delivery", Source = source,
            Target = target, Receiver = source.EventReceiver
        })).ToList();
        Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
        return 100 - target.Property.LifePoint!.Value;
    }

    private static void InstallCatalog(Context context, Unit unit, AbilityCatalog catalog, string id, int? level)
    {
        AbilityInstallHandler installer = unit.EventReceiver.Get<AbilityInstallHandler>()
            ?? throw new InvalidOperationException("The unit has no installer.");
        installer.AbilityFactory = (abilityId, abilityLevel) =>
            catalog.Require(abilityId).CreateHandlers(abilityLevel);
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = unit, Target = unit, AbilityIds = [id],
            Level = level, Receiver = unit.EventReceiver
        }).ToList();
    }

    private static void Place(MapRuntime map, Unit unit, Vector2D position)
    {
        var body = new MapObject(unit.Id.ToString("N"), position, unit.EventReceiver);
        map.AddObject(body);
        unit.MapObjects.Initialize(body);
    }

    private static int Hit(DamageTypeRegistry types)
    {
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        var source = CreateUnit(context, "source");
        var target = CreateUnit(context, "target");
        context.AddEventReceiver(source.EventReceiver);
        context.AddEventReceiver(target.EventReceiver);
        var schema = new ExpressionSchema<CombatFormulaContext>();
        schema.Seal();
        var ability = new DamageAbility
        {
            Id = "payload",
            Damages = [new DamageDefinition(types, "impact",
                NumericFormula<CombatFormulaContext>.Compile("40", schema))]
        };
        var trigger = new TriggerHandler
        {
            Id = "payload-trigger",
            TargetAbilityId = ability.Id,
            TriggerType = TriggerType.ProjectileHit,
            SourceAbilityIds = ["delivery"]
        };
        var receiver = new BeingDamageAbility { Id = "receiver", DamageTypes = types };
        receiver.SetMitigationFormula("impact", types.Require("impact").Formula
            ?? throw new InvalidOperationException("The selected type requires a mitigation formula."));
        Install(context, source, ability, trigger);
        Install(context, target, receiver);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        context.Dispatch(ProjectileHitPublicationEvent.FromHit(context.Factory.NewEvent<ProjectileHitEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            AbilityId = "delivery",
            Source = source,
            Target = target,
            Receiver = source.EventReceiver
        })).ToList();
        Assert.IsFalse(context.RunUntil(context.ContextTimeSpan).Terminated);
        return 100 - target.Property.LifePoint!.Value;
    }

    private static Unit CreateUnit(Context context, string id, int maximumLevel = Unit.DefaultMaximumLevel)
    {
        var unit = new Unit
        {
            Name = id,
            ArchetypeId = id,
            MaximumLevel = maximumLevel,
            Property = new UnitProperty { LifePoint = 100, LifePointMax = 100, Level = 1 },
            Abilities = new UnitAbilityManager(),
            EventReceiver = context.Factory.NewEventReceiver()
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler());
        unit.Initialize();
        return unit;
    }

    private static void Install(Context context, Unit unit, params IHandler[] handlers)
    {
        AbilityInstallHandler installer = unit.EventReceiver.Get<AbilityInstallHandler>()
            ?? throw new InvalidOperationException("The unit has no installer.");
        var definitions = handlers.ToDictionary(handler => handler.Id, StringComparer.Ordinal);
        installer.AbilityFactory = (id, _) => definitions.TryGetValue(id, out IHandler? handler)
            ? [handler]
            : throw new InvalidOperationException($"Unknown test ability '{id}'.");
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            Source = unit,
            Target = unit,
            AbilityIds = definitions.Keys.ToArray(),
            Receiver = unit.EventReceiver
        }).ToList();
    }

    private sealed class ArmorFieldModule(string id, double armor) : IContentModule
    {
        public string Id => id;

        public void Register(ContentSectionRegistry registry) =>
            registry.CombatFormulas().RegisterNumber("armor", _ => armor);
    }

    public sealed class DamageReferenceModule : IContentModule
    {
        public string Id => "damage-reference";

        public void Register(ContentSectionRegistry registry) =>
            registry.Register("damageRef", (yaml, context) =>
            {
                DamageReferenceDto dto = context.Deserialize<DamageReferenceDto>(yaml);
                context.RequireReference("damageType", dto.Type);
                return new DamageReference(dto.Type);
            });
    }

    public sealed class DamageReferenceDto
    {
        public string Type { get; set; } = "";
    }

    private sealed record DamageReference(string Type);

    public sealed class MarkerModule(MarkerParser parser) : IContentModule
    {
        public string Id => "marker-test";
        public void Register(ContentSectionRegistry registry) => registry.AbilityParsers().Register(parser);
    }

    public sealed class MarkerParser : IHandlerParser
    {
        private int created;
        public int Created => Volatile.Read(ref created);
        public string Type => "Marker";
        public string CompiledType { get; init; } = "Marker";
        public AbilityHandlerDefinition Compile(
            AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
        {
            MarkerDto dto = context.Deserialize<MarkerDto>(
                yaml ?? throw new InvalidDataException("Marker requires data."));
            int value = dto.Value;
            return new AbilityHandlerDefinition(definition, CompiledType, (level, _) =>
            {
                Interlocked.Increment(ref created);
                return [new MarkerAbility { Id = definition.Id, Value = value + level }];
            });
        }
    }

    public sealed class MarkerDto
    {
        public int Value { get; set; }
    }

    public sealed class MarkerAbility : AbilityBase
    {
        public int Value { get; init; }
    }
}
