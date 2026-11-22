using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Protocol.Command;
using Miaosoft.Sim.Loadout.Server;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;
using Miaosoft.Sim.Loadout.Trading.Policy;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

internal static class LoadoutArena
{
    public static async Task<LoopbackSession> StartAsync(
        CancellationToken token, bool existingDeath = false, bool dynamicFeatures = false)
    {
        var (simulation, units, world) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token);
        Context context = simulation.Context;
        bool transferred = false;
        try
        {
            UnitInventory[] inventories = units.Select(unit =>
            {
                var inventory = new UnitInventory(unit, new(2, 2, new Dictionary<string, string> { ["main"] = "tool" }));
                unit.EventReceiver.AddHandler(new InventoryHandler(inventory), new TradeHandler(inventory));
                return inventory;
            }).ToArray();
            var tree = new TalentTreeDefinition("training", "Training", 2,
                [new("basic", "Basic", [new("practice", "Practice", 2, [])])]);
            UnitTalents[] talents = units.Select(unit =>
            {
                var feature = new UnitTalents(unit, [tree]);
                unit.EventReceiver.AddHandler(new TalentDirectoryHandler(feature));
                context.Dispatch(context.Factory.NewEvent<TalentAuthorizationRequestEvent>() with
                {
                    Owner = unit, Definition = tree, Grant = new AbilitySourceInstance("Role", [], unit),
                    Authorize = true, Receiver = unit.EventReceiver
                }).ToList();
                return feature;
            }).ToArray();
            for (int index = 0; index < units.Count; index++)
            {
                Unit unit = units[index];
                var creation = context.Factory.NewEvent<ItemCreateRequestEvent>() with
                {
                    Coordinator = unit, Definition = new("focus", "Focus", "tool", [new("endurance", index + 1)]),
                    Receiver = unit.EventReceiver
                };
                context.Dispatch(creation).ToList();
                await Drain(context, token);
                ItemInstance item = unit.EventReceiver.History.OfType<ItemCreatedEvent>()
                    .Single(ev => ev.OperationId == creation.OperationId).Item;
                context.Dispatch(context.Factory.NewEvent<InventoryChangeRequestEvent>() with
                {
                    Owner = unit, Item = item, Operation = InventoryOperation.Acquire,
                    SlotId = "main", Receiver = unit.EventReceiver
                }).ToList();
                await Drain(context, token);
            }
            DeathTradeDirectory[] deaths = [];
            if (existingDeath)
            {
                Unit corpse = units[1], beneficiary = units[0];
                var directory = new DeathTradeDirectory(inventories[1], new Beneficiary(beneficiary), authorization =>
                    authorization.Corpse!.MapRuntime == beneficiary.MapObject!.MapRuntime &&
                    authorization.Corpse.MapRuntime!.TargetingDistance(
                        authorization.Corpse.Position, beneficiary.MapObject.Position) <= 12);
                corpse.EventReceiver.AddHandler(new DeathTradeHandler(directory));
                corpse.Killed();
                context.Dispatch(context.Factory.NewEvent<DeathResolvedEvent>() with
                {
                    DeathId = corpse.CurrentDeathId!.Value, Victim = corpse, Killer = beneficiary,
                    TargetObject = corpse.MapObject, Receiver = corpse.EventReceiver
                }).ToList();
                await Drain(context, token);
                deaths = [directory];
            }
            LoadoutParticipant[] participants = units.Select((unit, index) =>
                new LoadoutParticipant(unit, inventories[index], talents[index])).ToArray();
            LoadoutSessionSimulation adapter;
            if (dynamicFeatures)
            {
                var (npc, directory) = await PrepareNpc(context, units[0], token);
                ItemInstance originalNpcItem = npc.Inventory!.Items.Single().Item;
                var originalNpcAbilities = originalNpcItem.Source.Abilities.ToArray();
                var features = new LoadoutSessionFeatures(context, participants);
                int round = 0;
                context.EventProcessed += ev =>
                {
                    if (ev is UnitActionResultEvent { Success: true } action && action.Receiver == units[0].EventReceiver)
                    {
                        if (features.Exchanges.Values.Any(exchange => !exchange.IsClosed))
                            throw new InvalidOperationException("The previous exchange must settle before replacing its membership.");
                        round++;
                        if (round <= 2)
                            features = new(context, participants,
                                [new TradeSession(inventories[0], inventories[1], (first, second) =>
                                    first.CanParticipate && second.CanParticipate)]);
                        else if (round == 3)
                        {
                            features = new(context, participants.Append(npc), features.Exchanges.Values, [directory]);
                            world.Add(npc.Unit.Id, npc.Unit);
                            npc.Unit.Registry = units[0].Registry;
                            npc.Unit.Registry!.Register(npc.Unit);
                        }
                        else throw new InvalidOperationException("The dynamic Loadout scenario has already completed.");
                    }
                    else if (ev is DeathTradeResultEvent { Success: true } result &&
                        result.DeathId == npc.Unit.CurrentDeathId)
                    {
                        if (result.Operation == DeathTradeOperation.Claim)
                        {
                            if (!inventories[0].Items.Any(placement => ReferenceEquals(placement.Item, originalNpcItem)) ||
                                originalNpcItem.OwnerId != units[0].Id || originalNpcItem.Source.Holder != null ||
                                originalNpcItem.Source.Abilities.Count != originalNpcAbilities.Length ||
                                originalNpcItem.Source.Abilities.Zip(originalNpcAbilities)
                                    .Any(pair => !ReferenceEquals(pair.First, pair.Second) || pair.First.Level != 3))
                                throw new InvalidOperationException(
                                    "NPC pickup must retain original item/level-three ability objects in the beneficiary backpack without equipment authority.");
                            context.Dispatch(context.Factory.NewEvent<DeathTradeCleanupRequestEvent>() with
                            {
                                Directory = directory, ExpectedDeathId = result.DeathId, Receiver = npc.Unit.EventReceiver
                            }).ToList();
                        }
                        else if (result.Operation == DeathTradeOperation.Cleanup)
                            features = new(context, participants, features.Exchanges.Values);
                    }
                };
                adapter = new(simulation, () => features);
            }
            else
                adapter = new(simulation, participants,
                    [new TradeSession(inventories[0], inventories[1], (first, second) =>
                        first.CanParticipate && second.CanParticipate)], deaths);
            var runtime = new SessionRuntime(adapter, EventCodec.CreateBuilder().AddLoadout().Build(),
                [adapter.Observation], enableDemoAdmission: true);
            transferred = true;
            return await LoopbackSession.StartAsync(runtime, token);
        }
        finally
        {
            if (!transferred) context.Dispose();
        }
    }

    private static async Task<(LoadoutParticipant Npc, DeathTradeDirectory Directory)> PrepareNpc(
        Context context, Unit beneficiary, CancellationToken token)
    {
        var npc = new Unit
        {
            Name = "Keeper", ArchetypeId = "keeper", TeamId = 3, Abilities = new(),
            Property = new() { LifePoint = 1, LifePointMax = 10, Level = 1 },
            EventReceiver = context.Factory.NewEventReceiver()
        };
        npc.EventReceiver.AddHandler(new AbilityInstallHandler
        {
            AbilityFactory = beneficiary.EventReceiver.Get<AbilityInstallHandler>()!.AbilityFactory
        }, new UnitActionGateHandler());
        npc.Initialize();
        context.AddEventReceiver(npc.EventReceiver);
        var body = new MapObject(npc.Id.ToString("N"),
            new Vector2D(beneficiary.MapObject!.Position.X + 1, beneficiary.MapObject.Position.Y), npc.EventReceiver);
        beneficiary.MapObject.MapRuntime!.AddObject(body);
        npc.MapObjects.Initialize(body);
        var inventory = new UnitInventory(npc, new(1, 1, new Dictionary<string, string> { ["main"] = "tool" }));
        var directory = new DeathTradeDirectory(inventory, new Beneficiary(beneficiary), authorization =>
            authorization.Corpse!.MapRuntime == beneficiary.MapObject.MapRuntime &&
            authorization.Corpse.MapRuntime!.TargetingDistance(
                authorization.Corpse.Position, beneficiary.MapObject.Position) <= 12);
        npc.EventReceiver.AddHandler(new InventoryHandler(inventory), new TradeHandler(inventory), new DeathTradeHandler(directory));
        context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
        {
            CommandEventId = Guid.NewGuid(), Source = npc, Target = npc, Level = 1,
            AbilityIds = ["receiver", "mortality"], Receiver = npc.EventReceiver
        }).ToList();
        await Drain(context, token);
        var creation = context.Factory.NewEvent<ItemCreateRequestEvent>() with
        {
            Coordinator = npc, Definition = new("focus", "Focus", "tool", [new("endurance", 3)]),
            Receiver = npc.EventReceiver
        };
        context.Dispatch(creation).ToList();
        await Drain(context, token);
        ItemInstance item = npc.EventReceiver.History.OfType<ItemCreatedEvent>()
            .Single(ev => ev.OperationId == creation.OperationId).Item;
        context.Dispatch(context.Factory.NewEvent<InventoryChangeRequestEvent>() with
        {
            Owner = npc, Item = item, Operation = InventoryOperation.Acquire, SlotId = "main", Receiver = npc.EventReceiver
        }).ToList();
        await Drain(context, token);
        return (new(npc, inventory), directory);
    }

    private static async Task Drain(Context context, CancellationToken token)
    {
        while (context.HasEventsDueNow)
            if (!(await context.NextAsync(token)).KeepRunning)
                throw new InvalidOperationException("Loadout setup terminated.");
    }

    private sealed class Beneficiary(Unit unit) : IDeathTradeDistributionPolicy
    {
        public IEnumerable<DeathTradeAllocation> Allocate(DeathResolvedEvent death, IReadOnlyList<ItemInstance> items)
        {
            yield return new(unit, items.Select(item => item.InstanceId).ToArray());
        }
    }
}
