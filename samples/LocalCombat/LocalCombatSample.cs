using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;
using Miaosoft.Sim.Core;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Miaosoft.Sim.Samples.LocalCombat;

[TestClass]
public sealed class LocalCombatSample
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void LoadedAbilitiesExecuteAnIndependentCombatChain()
    {
        AbilityCatalog catalog = LoadCatalog();
        Guid firstInstance = RunScenario(catalog, 1);
        Guid secondInstance = RunScenario(catalog, 2);
        Assert.AreNotEqual(firstInstance, secondInstance);
        TestContext.WriteLine("Both scenes shared compiled definitions but used distinct ability instances.");
    }

    [TestMethod]
    public void PassiveAndStatusFormulasStackExpireAndUninstall()
    {
        AbilityCatalog catalog = LoadCatalog();
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        Unit source = CreateUnit(context, catalog, "source");
        Unit target = CreateUnit(context, catalog, "target");
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"], [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        Install(context, source, ["bolt", "fortify"]);
        Install(context, target, ["endurance"]);
        context.AdvanceTo(TimeSpan.Zero);
        Assert.AreEqual(110, target.Property.LifePointMax);
        ProjectileAbility bolt = source.EventReceiver.GetHandlers<ProjectileAbility>().Single();
        for (int cast = 0; cast < 2; cast++)
        {
            context.AdvanceTo(TimeSpan.FromSeconds(cast));
            context.Dispatch(context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = bolt.Id, AbilityInstanceId = bolt.RuntimeInstanceId,
                Source = source, Target = target, Receiver = source.EventReceiver
            }).ToList();
            context.AdvanceTo(TimeSpan.FromSeconds(cast));
            Assert.AreEqual(cast == 0 ? 131 : 152, target.Property.LifePointMax);
        }
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.AreEqual(110, target.Property.LifePointMax);
        Assert.IsEmpty(target.Abilities.GetAbilitiesByLabel(AbilityLabel.Status));
        context.Dispatch(context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = target, Target = target,
            AbilityIds = ["endurance"], Receiver = target.EventReceiver
        }).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(5));
        Assert.AreEqual(100, target.Property.LifePointMax);
        TestContext.WriteLine("Life maximum: 100 -> 110 passive -> 131 -> 152 -> 110 on expiry -> 100 on uninstall.");
    }

    [TestMethod]
    [DataRow("event.source == holder && event.target != holder", true)]
    [DataRow("event.ability != listener.ability && event.ability.definition == \"bolt\" && listener.ability.definition == \"condition-example\"", true)]
    [DataRow("event.ability.source == null && listener.ability.source == null", true)]
    [DataRow("event.ability.source.category != \"Equipment\"", false)]
    [DataRow("event.ability.source == listener.ability.source", false)]
    [DataRow("event.ability.source.category == null || listener.ability.source.category != \"Equipment\"", true)]
    [DataRow("!false && true || false", true)]
    [DataRow("(2 + 3) * 4 / 2 - 1 == 9 && -2 < +1 && 2 >= 2 && 2 <= 3 && 3 > 2", true)]
    [DataRow("min(3, 2, 1) == 1 && MAX(1, 3) == 3 && clamp(5, 0, 3) == 3", true)]
    [DataRow("floor(1.9) == 1 && ceil(1.1) == 2 && ceiling(1.1) == 2 && round(2.5) == 2 && abs(-2) == 2", true)]
    [DataRow("\"Bolt\" != \"bolt\" && (true == !false)", true)]
    [DataRow("true || 1 / 0 > 0", true)]
    [DataRow("false && 1 / 0 > 0", false)]
    [DataRow("abs((0.1 + 0.2) - 0.3) < 0.000001", true)]
    [DataRow("target.life > 0 && target.life <= 100", true)]
    public void TypedConditionExamplesRunThroughTheCombatChain(string expression, bool matches)
    {
        AbilityCatalog catalog = LoadCatalog(expression);
        RunScenario(catalog, 1, matches);
        TestContext.WriteLine($"condition={expression}; matched={matches}");
    }

    [TestMethod]
    [DataRow("1 == \"1\"")]
    [DataRow("true > false")]
    [DataRow("\"a\" < \"b\"")]
    [DataRow("holder == event.ability")]
    [DataRow("event.ability.source == \"00000000-0000-0000-0000-000000000000\"")]
    [DataRow("holder.level > 0")]
    [DataRow("min(1) > 0")]
    [DataRow("false && missing.field > 0")]
    public void InvalidConditionExamplesFailBeforeSimulation(string expression)
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() => LoadCatalog(expression));
        StringAssert.Contains(error.Message, "condition-example.yaml");
        TestContext.WriteLine($"Rejected at load: {expression}; {error.Message}");
    }

    private static AbilityCatalog LoadCatalog(string? expression = null)
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent();
        services.AddAbilityContent();
        services.AddSingleton<IContentModule>(new SampleFormulaFields());
        using ServiceProvider provider = services.BuildServiceProvider();
        string file = Path.Combine(AppContext.BaseDirectory, "combat.yaml");
        var documents = new List<ContentDocument> { new(file, File.ReadAllText(file)) };
        if (expression != null)
            documents.Add(new ContentDocument("condition-example.yaml", $$"""
                schemaVersion: 1
                ability:
                  - id: condition-example
                    type: Projectile
                    allowMultiple: false
                    trigger:
                      type: ProjectileHit
                      sourceAbilityIds: [bolt]
                      conditions:
                        - { type: Expression, expression: '{{expression.Replace("'", "''")}}' }
                    data: { speed: 0 }
                """));
        return provider.GetRequiredService<IContentCatalogLoader>().Load(
            documents,
            [SkillContentServiceCollectionExtensions.DamageModuleId,
                SkillContentServiceCollectionExtensions.AbilityModuleId, "sample-fields"])
            .GetSection<AbilityCatalog>("ability");

    }

    private Guid RunScenario(AbilityCatalog catalog, int scene, bool? conditionMatches = null)
    {
        using var context = new Context(causalityFactory: new TriggerChainFactory());
        context.Logger.Output = null;
        var timeline = new List<IEvent>();
        context.EventProcessed += timeline.Add;
        Unit source = CreateUnit(context, catalog, "source");
        Unit target = CreateUnit(context, catalog, "target");
        source.Property.LifePoint = 50;
        source.EventReceiver.AddHandler(new BallisticTravelHandler(), new MapMovementHandler());
        var map = new MapRuntime();
        Place(map, source, new Vector2D(10, 10));
        Place(map, target, new Vector2D(20, 10));
        var registry = new UnitSpatialRegistry();
        registry.Register(source);
        registry.Register(target);
        source.Registry = target.Registry = registry;
        source.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
            new MapVisibilityScene(["0"], [new MapVisibilityObserver("0", source.MapObject!, 100)],
                map.Objects.Select(body => new MapVisibilityTarget(body, 0)).ToArray()));
        Install(context, source, ["bolt", "bolt-damage", "heal-delivery", "heal-payload"]);
        if (conditionMatches.HasValue) Install(context, source, ["condition-example"]);
        Install(context, target, ["receiver"]);
        Run(context);
        ProjectileAbility bolt = source.EventReceiver.GetHandlers<ProjectileAbility>()
            .Single(ability => ability.Id == "bolt");
        Assert.AreEqual(2, bolt.GateManager!.GetChargeSnapshot()!.Available);

        for (int cast = 1; cast <= 3; cast++)
        {
            int start = timeline.Count;
            context.Dispatch(context.Factory.NewEvent<CommandEvent>() with
            {
                CommandInstanceId = Guid.NewGuid(),
                AbilityId = "bolt",
                AbilityInstanceId = bolt.RuntimeInstanceId,
                Source = source,
                Target = target,
                Receiver = source.EventReceiver
            }).ToList();
            Run(context);
            CommandExecutionEvent result = source.EventReceiver.History.OfType<CommandExecutionEvent>().Last();
            int expectedLife = cast switch { 1 => 79, _ => 58 };
            int expectedSourceLife = cast == 1 ? 57 : 64;
            decimal expectedEnergy = cast == 1 ? 15 : 10;
            int[] expectedDamage = cast == 1 ? [21] : [21, 21];
            int[] expectedHealing = cast == 1 ? [7] : [7, 7];
            CommandExecutionStatus expectedStatus = cast < 3
                ? CommandExecutionStatus.Success : CommandExecutionStatus.Failure;
            CommandExecutionFailureReason expectedReason = cast < 3
                ? CommandExecutionFailureReason.None : CommandExecutionFailureReason.ChargesDepleted;
            Assert.AreEqual(expectedLife, target.Property.LifePoint);
            Assert.AreEqual(expectedSourceLife, source.Property.LifePoint);
            Assert.AreEqual(expectedEnergy, source.Property.Energy);
            CollectionAssert.AreEqual(expectedDamage, target.EventReceiver.History.OfType<DamageLifeReduceEvent>()
                .Select(damage => damage.LifePointReduce).ToArray());
            CollectionAssert.AreEqual(expectedHealing, source.EventReceiver.History.OfType<HealLifeGainEvent>()
                .Select(heal => heal.LifeGain).ToArray());
            Assert.AreEqual(expectedStatus, result.Status);
            Assert.AreEqual(expectedReason, result.FailureReason);
            string[] actualChain = timeline.Skip(start).Select(ev => ev switch
            {
                ProjectileEvent projectile when projectile.AbilityId == "bolt" => "bolt launched",
                ProjectileHitEvent hit when hit.AbilityId == "bolt" && hit.Receiver == source.EventReceiver => "bolt hit",
                DamageLifeReduceEvent damage when damage.Receiver == source.EventReceiver => "damage confirmed",
                ProjectileEvent projectile when projectile.AbilityId == "heal-delivery" => "heal launched",
                ProjectileHitEvent hit when hit.AbilityId == "heal-delivery" && hit.Receiver == source.EventReceiver => "heal hit",
                HealLifeGainEvent heal when heal.Receiver == source.EventReceiver => "heal confirmed",
                _ => null
            }).OfType<string>().ToArray();
            CollectionAssert.AreEqual(cast < 3
                ? new[] { "bolt launched", "bolt hit", "damage confirmed", "heal launched", "heal hit", "heal confirmed" }
                : Array.Empty<string>(), actualChain);
            TestContext.WriteLine($"scene={scene} cast={cast} status={result.Status} reason={result.FailureReason}" +
                $" target-life={expectedLife} source-life={expectedSourceLife} source-energy={expectedEnergy}");
            if (actualChain.Length > 0) TestContext.WriteLine(string.Join(" -> ", actualChain));
        }

        if (conditionMatches.HasValue)
            Assert.HasCount(conditionMatches.Value ? 2 : 0,
                source.EventReceiver.History.OfType<ProjectileEvent>().Where(ev => ev.AbilityId == "condition-example"));
        return bolt.RuntimeInstanceId;
    }

    private static Unit CreateUnit(Context context, AbilityCatalog catalog, string name)
    {
        var unit = new Unit
        {
            Name = name,
            ArchetypeId = name,
            Property = new UnitProperty
            {
                LifePoint = 100, LifePointMax = 100, Level = 1,
                Energy = 20, EnergyMax = 20, EnergyRecoveryPerSecond = 0
            },
            Abilities = new UnitAbilityManager(),
            EventReceiver = context.Factory.NewEventReceiver()
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler
        {
            AbilityFactory = (id, level) => catalog.Require(id).CreateHandlers(level)
        }, new UnitActionGateHandler());
        unit.Initialize();
        context.AddEventReceiver(unit.EventReceiver);
        return unit;
    }

    private static void Place(MapRuntime map, Unit unit, Vector2D position)
    {
        var body = new MapObject(unit.Id.ToString("N"), position, unit.EventReceiver);
        map.AddObject(body);
        unit.MapObjects.Initialize(body);
    }

    private static void Install(Context context, Unit unit, string[] abilities)
    {
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(),
            Source = unit,
            Target = unit,
            AbilityIds = abilities,
            Level = 2,
            Receiver = unit.EventReceiver
        }).ToList();
    }

    private static void Run(Context context) => Assert.IsFalse(context.RunUntilQueueEmpty().Terminated);

    private sealed class SampleFormulaFields : IContentModule
    {
        public string Id => "sample-fields";
        public void Register(ContentSectionRegistry registry)
        {
            registry.TriggerConditions().For(TriggerType.ProjectileHit)
                .RegisterNumber("target.life", value => value.EventTarget?.Property.LifePoint, nullable: true);
        }
    }
}
