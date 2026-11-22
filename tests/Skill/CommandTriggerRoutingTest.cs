using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class CommandTriggerRoutingTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void DirectAndObservedCommandsUseTheirOwnHolders()
    {
        using var battle = new Battle();
        Recorder direct = battle.Install(battle.Source, "fire", TriggerSourceScope.Self);
        Recorder ally = battle.Install(battle.Ally, "respond", TriggerSourceScope.Ally, "fire");
        Recorder enemy = battle.Install(battle.Enemy, "respond", TriggerSourceScope.Enemy, "fire");
        Recorder unrelated = battle.Install(battle.Other, "respond", TriggerSourceScope.Self, "fire");

        CommandEvent command = battle.Command();

        Assert.HasCount(1, direct.Invocations);
        Assert.HasCount(1, ally.Invocations);
        Assert.HasCount(1, enemy.Invocations);
        Assert.IsEmpty(unrelated.Invocations);
        Assert.IsTrue(direct.Invocations[0].IsDirectCommand);
        Assert.AreSame(command, direct.Invocations[0].DirectCommand);
        Assert.IsFalse(ally.Invocations[0].IsDirectCommand);
        Assert.IsNull(ally.Invocations[0].DirectCommand);
        Assert.AreSame(battle.Ally, ally.Invocations[0].Source);
        Assert.AreSame(battle.Enemy, enemy.Invocations[0].Source);
        Assert.AreSame(battle.Enemy, ally.Invocations[0].Target);
        TestContext.WriteLine("The requested ability ran as a direct command; ally and enemy listeners responded as their own owners.");
    }

    [TestMethod]
    public void ScopeIsEvaluatedAfterEarlierCommandHandlersChangeTeams()
    {
        using var battle = new Battle();
        battle.Install(battle.Source, "fire", TriggerSourceScope.Self);
        battle.Source.EventReceiver.AddHandler(new ChangeTeam(battle.Ally, 2));
        Recorder observer = battle.Install(battle.Ally, "respond", TriggerSourceScope.Ally, "fire");

        battle.Command();

        Assert.AreEqual(2, battle.Ally.TeamId);
        Assert.IsEmpty(observer.Invocations);
        TestContext.WriteLine("A prior command handler changed the team before the observer evaluated its scope.");
    }

    [TestMethod]
    public void MissingAndUnauthorizedCommandsAreRejectedBeforeObservers()
    {
        using var battle = new Battle();
        Recorder direct = battle.Install(battle.Source, "fire", TriggerSourceScope.Self);
        Recorder observer = battle.Install(battle.Ally, "respond", TriggerSourceScope.Ally, "fire");
        var binding = new AtomicTransaction();
        direct.Binding.StageBind(binding, new TestAbilitySource(Guid.NewGuid()));
        direct.Binding.StageAuthority(binding, battle.Context, battle.Ally.Id);
        binding.Commit();

        CommandEvent unknown = battle.NewCommand() with { AbilityId = "unknown" };
        CommandEvent unauthorized = battle.NewCommand();
        battle.Context.Dispatch(unknown).ToList();
        battle.Context.Dispatch(unauthorized).ToList();
        battle.Run();

        Assert.IsEmpty(direct.Invocations);
        Assert.IsEmpty(observer.Invocations);
        CommandExecutionEvent[] failures = battle.Source.EventReceiver.History.OfType<CommandExecutionEvent>()
            .Where(item => ReferenceEquals(item.Command, unknown) || ReferenceEquals(item.Command, unauthorized))
            .ToArray();
        Assert.HasCount(2, failures);
        Assert.IsTrue(failures.All(item => item.Status == CommandExecutionStatus.Failure &&
            item.FailureReason == CommandExecutionFailureReason.AbilityNotAvailable));
        TestContext.WriteLine("Unknown and unauthorized omitted-instance commands failed before direct or third-party observers ran.");
    }

    [TestMethod]
    public void CancellationBeforeACommandTriggerSkipsItsProbability()
    {
        var random = new CountingRandom();
        using var battle = new Battle(random);
        using var cancellation = new CancellationTokenSource();
        battle.Source.EventReceiver.AddHandler(new CancelCommand(cancellation));
        Recorder direct = battle.Install(battle.Source, "fire", TriggerSourceScope.Self, probability: 0.5);

        battle.Command(cancellation);

        Assert.AreEqual(0, random.Draws);
        Assert.IsEmpty(direct.Invocations);
        TestContext.WriteLine("A prior command handler canceled the command before the trigger drew probability.");
    }

    [TestMethod]
    public void TheSameCommandCannotBeDispatchedTwiceOrToAnotherReceiver()
    {
        using var battle = new Battle();
        Recorder direct = battle.Install(battle.Source, "fire", TriggerSourceScope.Self);
        CommandEvent command = battle.Command();

        battle.Context.Dispatch(command).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => battle.Run());
        Assert.HasCount(1, direct.Invocations);

        using var other = new Battle();
        var invalid = other.NewCommand() with { Receiver = other.Enemy.EventReceiver };
        other.Context.Dispatch(invalid).ToList();
        Assert.ThrowsExactly<InvalidOperationException>(() => other.Run());
        TestContext.WriteLine("Repeated publication and a receiver other than the command source were rejected.");
    }

    private sealed class Recorder : AbilityBase, ICommandAbility
    {
        public List<AbilityTriggerInvokeEvent> Invocations { get; } = [];
        public CommandGateManager? GateManager => null;

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId == Id) Invocations.Add(ev);
            yield break;
        }
    }

    private sealed class ChangeTeam(Unit unit, int team) : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCommand(CommandEvent ev)
        {
            unit.TeamId = team;
            yield break;
        }
    }

    private sealed class CancelCommand(CancellationTokenSource cancellation) : Miaosoft.Sim.Combat.Skill.Handler.Handler
    {
        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCommand(CommandEvent ev)
        {
            cancellation.Cancel();
            yield break;
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

    private sealed class Battle : IDisposable
    {
        public Battle(Random? random = null)
        {
            Context = new Context(causalityFactory: new TriggerChainFactory(), random: random);
            Context.Logger.Output = null;
            Source = CreateUnit("source", 1);
            Ally = CreateUnit("ally", 1);
            Enemy = CreateUnit("enemy", 2);
            Other = CreateUnit("other", 2);
        }

        public Context Context { get; }
        public Unit Source { get; }
        public Unit Ally { get; }
        public Unit Enemy { get; }
        public Unit Other { get; }

        public Recorder Install(Unit holder, string target, TriggerSourceScope scope,
            string? observed = null, double? probability = null)
        {
            var recorder = new Recorder { Id = target };
            var trigger = new TriggerHandler
            {
                Id = $"trigger-{holder.Name}",
                TargetAbilityId = target,
                Config = new AbilityTriggerConfig
                {
                    Type = TriggerType.Command,
                    SourceAbilityIds = observed == null ? null : [observed],
                    EventSourceScope = scope,
                    Probability = probability
                }
            };
            TestHandlerFactory.Register(holder, recorder, trigger);
            Context.Dispatch(Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = holder, Target = holder,
                AbilityIds = [recorder.Id, trigger.Id], Receiver = holder.EventReceiver
            }).ToList();
            Run();
            return recorder;
        }

        public CommandEvent NewCommand(CancellationTokenSource? cancellation = null) =>
            Context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "fire", Source = Source, Target = Enemy, Receiver = Source.EventReceiver,
                CancellationTokenSource = cancellation
            };

        public CommandEvent Command(CancellationTokenSource? cancellation = null)
        {
            CommandEvent command = NewCommand(cancellation);
            Context.Dispatch(command).ToList();
            Run();
            return command;
        }

        public void Run() => Assert.IsFalse(Context.RunUntil(TimeSpan.Zero).Terminated);
        public void Dispose() => Context.Dispose();

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
    }
}
