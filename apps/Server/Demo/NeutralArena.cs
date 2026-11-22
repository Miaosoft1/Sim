using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Hex;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Server;
using Miaosoft.Sim.Server.Observation;

namespace Miaosoft.Sim.Samples.NetworkSession;

public static class NeutralArena
{
    public static async Task<SessionRuntime> CreateAsync(
        string contentPath, bool enableDemoAdmission = false,
        IEnumerable<ISessionObservationModule>? modules = null, int inputCapacity = 1024,
        CancellationToken cancellationToken = default)
    {
        var (simulation, _, _) = await PrepareAsync(contentPath, cancellationToken).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            var runtime = new SessionRuntime(simulation, EventCodec.CreateBuilder().Build(), modules, enableDemoAdmission, inputCapacity);
            transferred = true;
            return runtime;
        }
        finally
        {
            if (!transferred) simulation.Context.Dispose();
        }
    }

    public static async Task<(BattleSessionSimulation Simulation, IReadOnlyList<Unit> Units,
        Dictionary<Guid, Unit> WorldUnits)> PrepareAsync(
        string contentPath, CancellationToken cancellationToken = default, HexBoardLayout? boardLayout = null)
    {
        AbilityCatalog catalog = Load(contentPath);
        if (boardLayout != null && boardLayout.Definition.PlayableCells.Count < 2)
            throw new ArgumentException("The arena needs at least two playable cells.", nameof(boardLayout));
        HexBoardMap? board = boardLayout == null ? null : new(
            boardLayout.Definition, boardLayout.CellSize, boardLayout.Orientation, "arena.hex", "Hex arena",
            speedUnitsPerCell: 1);
        var context = new Context(realtime: true, timeScale: 0, causalityFactory: new TriggerChainFactory());
        bool transferred = false;
        try
        {
            context.Logger.Output = null;
            var map = board?.Map ?? new MapRuntime();
            HexCoordinate[] cells = board == null ? [] :
                board.Layout.Definition.PlayableCells.OrderBy(cell => cell.Q).ThenBy(cell => cell.R).ToArray();
            var registry = new Miaosoft.Sim.Combat.Skill.Targeting.UnitSpatialRegistry();
            Unit[] units = [CreateUnit(context, catalog, "Alpha", 1), CreateUnit(context, catalog, "Beta", 2)];
            var world = units.ToDictionary(unit => unit.Id);
            for (int index = 0; index < units.Length; index++)
            {
                Unit unit = units[index];
                var body = new MapObject(unit.Id.ToString("N"), new Vector2D(10 + index * 10, 10), unit.EventReceiver);
                if (board == null)
                {
                    map.AddObject(body);
                    unit.MapObjects.Initialize(body);
                }
                else
                {
                    unit.MapObjects.Initialize(body);
                    if (!board.TryPlace(body, cells[index]))
                        throw new InvalidOperationException("The arena participant could not enter its configured cell.");
                }
                registry.Register(unit);
                unit.Registry = registry;
                string team = unit.TeamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                unit.VisibilityQuery = () => new MapVisibilitySnapshot(MapVisibilityArea.Default,
                    new MapVisibilityScene([team], [new MapVisibilityObserver(team, unit.MapObject!, 100)],
                        map.Objects.Select(entry => new MapVisibilityTarget(entry, 0)).ToArray()));
                context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
                {
                    CommandEventId = Guid.NewGuid(), Source = unit, Target = unit, Level = 2,
                    AbilityIds = ["bolt", "moving-bolt", "bolt-damage", "heal-delivery", "heal-payload", "fortify", "receiver"],
                    Receiver = unit.EventReceiver
                }).ToList();
            }
            while (context.HasEventsDueNow)
            {
                ContextStepResult step = await context.NextAsync(cancellationToken).ConfigureAwait(false);
                if (!step.KeepRunning) throw new InvalidOperationException("Arena initialization terminated.");
            }
            var simulation = new BattleSessionSimulation(context, units, _ => Array.AsReadOnly(
                world.Values.Where(unit => unit.MapObject?.MapRuntime == map)
                    .Select(unit => new ObservedUnit(unit, unit.MapObject!)).ToArray()), () => world);
            transferred = true;
            return (simulation, Array.AsReadOnly(units), world);
        }
        finally
        {
            if (!transferred) context.Dispose();
        }
    }

    private static Unit CreateUnit(Context context, AbilityCatalog catalog, string name, int team)
    {
        var unit = new Unit
        {
            Name = name, ArchetypeId = "arena-participant", TeamId = team,
            Property = new UnitProperty
            {
                LifePoint = 50, LifePointMax = 100, Level = 1, MoveSpeedBase = 5,
                Energy = 20, EnergyMax = 20, EnergyRecoveryPerSecond = 0
            },
            Abilities = new UnitAbilityManager(), EventReceiver = context.Factory.NewEventReceiver()
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler
        {
            AbilityFactory = (id, level) => catalog.Require(id).CreateHandlers(level)
        }, new UnitActionGateHandler(), new UnitMovementHandler(), new MapMovementHandler(), new BallisticTravelHandler());
        unit.Initialize();
        context.AddEventReceiver(unit.EventReceiver);
        return unit;
    }

    private static AbilityCatalog Load(string path)
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddDamageTypeContent();
        services.AddAbilityContent();
        services.AddSingleton<IContentModule>(new ArenaFormulaFields());
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IContentCatalogLoader>().Load([new(path, File.ReadAllText(path))],
            [SkillContentServiceCollectionExtensions.DamageModuleId, SkillContentServiceCollectionExtensions.AbilityModuleId,
                "arena-fields"]).GetSection<AbilityCatalog>("ability");
    }

    private sealed class ArenaFormulaFields : IContentModule
    {
        public string Id => "arena-fields";
        public void Register(ContentSectionRegistry registry)
        {
            registry.TriggerConditions().For(TriggerType.ProjectileHit)
                .RegisterNumber("target.life", value => value.EventTarget?.Property.LifePoint, nullable: true);
        }
    }
}
