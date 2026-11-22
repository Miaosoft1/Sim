using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class TriggerChainTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RepeatedDefinitionsContinueUntilThePathLimitThenReportInProduction()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        using var battle = new SignalBattle(new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add));
        var echo = new EchoHandler();
        battle.Install(echo, Trigger("to-a", "A"), Trigger("to-b", "B"));

        AbilitySignalEvent root = battle.Signal("to-a");
        battle.Run();

        CollectionAssert.AreEqual(Enumerable.Range(0, 16)
            .Select(index => index % 2 == 0 ? "A" : "B").ToArray(), echo.Invocations.ToArray());
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.PathDepth, diagnostics[0].Reason);
        Assert.AreEqual(16, diagnostics[0].Depth);
        Assert.AreEqual(16, ((AbilityTriggerChain)root.Causality!).TriggerCount);
        Assert.AreEqual("A", diagnostics[0].AbilityId);
        TestContext.WriteLine("A → B → A continued through intermediate events for 16 activations; the 17th was diagnosed and stopped.");
    }

    [TestMethod]
    public void StrictModeThrowsAtThePathLimitInsteadOfSilentlyDroppingTheCycle()
    {
        using var battle = new SignalBattle(new TriggerChainFactory());
        var echo = new EchoHandler();
        battle.Install(echo, Trigger("to-a", "A"), Trigger("to-b", "B"));
        battle.Signal("to-a");

        var error = Assert.ThrowsExactly<TriggerLimitExceededException>(battle.Run);
        Assert.AreEqual(TriggerLimitReason.PathDepth, error.Diagnostic.Reason);
        Assert.HasCount(16, echo.Invocations);
        TestContext.WriteLine("Strict mode raised a path-limit exception after 16 real activations.");
    }

    [TestMethod]
    public void SeparateBranchesShareTheConfiguredTotalBudgetButNewRootsDoNot()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        using var battle = new SignalBattle(new TriggerChainFactory(
            totalBudget: 3, mode: TriggerLimitMode.Production, report: diagnostics.Add));
        var fork = new ForkHandler(maximumDepth: 4);
        battle.Install(fork, Trigger("fork", "branch"));
        AbilitySignalEvent first = battle.Signal("fork");
        battle.Run();
        Assert.AreEqual(3, fork.Count);
        Assert.HasCount(1, diagnostics);
        Assert.IsTrue(diagnostics.All(diagnostic =>
            diagnostic.Reason == TriggerLimitReason.TotalBudget && diagnostic.TriggerCount == 3));

        AbilitySignalEvent second = battle.Signal("fork");
        battle.Run();
        Assert.AreEqual(6, fork.Count);
        Assert.AreEqual(3, ((AbilityTriggerChain)first.Causality!).TriggerCount);
        Assert.AreEqual(3, ((AbilityTriggerChain)second.Causality!).TriggerCount);
        Assert.AreNotSame(first.Causality, second.Causality);
        Assert.HasCount(2, diagnostics);
        TestContext.WriteLine("Sibling branches shared a three-trigger budget; a separate root received its own budget.");
    }

    [TestMethod]
    public void StrictModeThrowsWhenTheSharedBudgetIsExhausted()
    {
        using var battle = new SignalBattle(new TriggerChainFactory(totalBudget: 3));
        var fork = new ForkHandler(maximumDepth: 4);
        battle.Install(fork, Trigger("fork", "branch"));
        battle.Signal("fork");

        var error = Assert.ThrowsExactly<TriggerLimitExceededException>(battle.Run);
        Assert.AreEqual(TriggerLimitReason.TotalBudget, error.Diagnostic.Reason);
        Assert.AreEqual(3, error.Diagnostic.TriggerCount);
        Assert.AreEqual(3, fork.Count);
        TestContext.WriteLine("Strict mode reported the shared budget limit without rolling back earlier activations.");
    }

    [TestMethod]
    public void InvalidConfigurationAndAMissingFactoryFailExplicitly()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TriggerChainFactory(totalBudget: 0));
        Assert.ThrowsExactly<ArgumentException>(() => new TriggerChainFactory(mode: TriggerLimitMode.Production));
        using var battle = new SignalBattle(null);
        battle.Install(new EchoHandler(), Trigger("to-a", "A"));
        battle.Signal("to-a");
        var error = Assert.ThrowsExactly<InvalidOperationException>(battle.Run);
        StringAssert.Contains(error.Message, "TriggerChainFactory");
        TestContext.WriteLine("Zero capacity, unreported production limits and triggers without a chain factory failed explicitly.");
    }

    [TestMethod]
    public void TriggerProbabilityUsesTheMatchRandomOnlyAfterConditionsMatch()
    {
        var random = new SequenceRandom(0.2, 0.8);
        using var battle = new SignalBattle(new TriggerChainFactory(), random);
        var recorder = new InvocationRecorder();
        battle.Install(recorder,
            ProbabilityTrigger("unmatched", "other", 0.5),
            ProbabilityTrigger("rejected", "pulse", 0.5, new RejectedCondition()),
            ProbabilityTrigger("first", "pulse", 0.5),
            ProbabilityTrigger("second", "pulse", 0.5),
            ProbabilityTrigger("always", "pulse", 1),
            ProbabilityTrigger("never", "pulse", 0));
        battle.Signal("pulse");
        battle.Run();

        CollectionAssert.AreEqual(new[] { "first", "always" }, recorder.Invocations.ToArray());
        Assert.AreEqual(2, random.Draws);

        var otherRandom = new SequenceRandom(0.9);
        using var other = new SignalBattle(new TriggerChainFactory(), otherRandom);
        var otherRecorder = new InvocationRecorder();
        other.Install(otherRecorder, ProbabilityTrigger("first", "pulse", 0.5));
        other.Signal("pulse");
        other.Run();
        Assert.IsEmpty(otherRecorder.Invocations);
        Assert.AreEqual(1, otherRandom.Draws);
        Assert.AreEqual(2, random.Draws);
        TestContext.WriteLine("Unmatched, rejected, zero and certain triggers did not draw; two matching triggers used separate draws from their match's random source.");
    }

    [TestMethod]
    public void InvalidTriggerProbabilitiesAndRandomValuesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AbilityTriggerConfig { Probability = -0.1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AbilityTriggerConfig { Probability = 1.1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AbilityTriggerConfig { Probability = double.NaN });
        using var battle = new SignalBattle(new TriggerChainFactory(), new SequenceRandom(1));
        battle.Install(new InvocationRecorder(), ProbabilityTrigger("bad", "pulse", 0.5));
        battle.Signal("pulse");
        var error = Assert.ThrowsExactly<InvalidOperationException>(battle.Run);
        StringAssert.Contains(error.Message, "random source");
        TestContext.WriteLine("Invalid probabilities and a random source outside [0, 1) failed explicitly.");
    }

    [TestMethod]
    public void RejectedDeathConditionsDoNotConsumeProbability()
    {
        var random = new SequenceRandom();
        using var battle = new SignalBattle(new TriggerChainFactory(), random);
        var recorder = new InvocationRecorder();
        battle.Install(recorder, new TriggerHandler
        {
            Id = "death-trigger", TargetAbilityId = "death-reaction",
            Config = new AbilityTriggerConfig
            {
                Type = TriggerType.Death, EventSourceScope = TriggerSourceScope.Any,
                Probability = 0.5, Conditions = [new RejectedCondition()]
            }
        });
        battle.Death();
        battle.Run();

        Assert.AreEqual(0, random.Draws);
        Assert.IsEmpty(recorder.Invocations);
        TestContext.WriteLine("A rejected confirmed-death condition did not draw probability or activate its ability.");
    }

    [TestMethod]
    public void DefaultBudgetAllows4096TriggersAcrossBranchesAndRejectsTheRest()
    {
        int rejected = 0;
        using var battle = new SignalBattle(new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostic =>
            {
                Assert.AreEqual(TriggerLimitReason.TotalBudget, diagnostic.Reason);
                Assert.AreEqual(4096, diagnostic.Limit);
                rejected++;
            }));
        var fork = new ForkHandler(maximumDepth: 13);
        battle.Install(fork, Trigger("fork", "branch"));
        AbilitySignalEvent root = battle.Signal("fork");
        battle.Run();

        Assert.AreEqual(4096, fork.Count);
        Assert.AreEqual(4096, ((AbilityTriggerChain)root.Causality!).TriggerCount);
        Assert.AreEqual(1, rejected);
        TestContext.WriteLine("The default root admitted exactly 4096 branch activations and reported budget exhaustion once.");
    }

    [TestMethod]
    public void DerivedDelayedCommandsWithNewIdsDoNotStartNewChains()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        using var battle = new SignalBattle(new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add));
        var loop = new CommandLoopHandler();
        battle.Install(loop, new TestCommandAbility { Id = "command-loop" }, new TriggerHandler
        {
            Id = "command-trigger", TargetAbilityId = "command-loop", TriggerType = TriggerType.Command
        });

        CommandEvent first = battle.Command("command-loop");
        battle.Run();
        Assert.AreEqual(16, loop.Count);
        Assert.AreEqual(16, loop.CommandIds.Distinct().Count());
        Assert.AreEqual(TimeSpan.FromSeconds(16), battle.CurrentTime);
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.PathDepth, diagnostics[0].Reason);

        CommandEvent second = battle.Command("command-loop");
        battle.Run();
        Assert.AreEqual(32, loop.Count);
        Assert.AreNotSame(first.Causality, second.Causality);
        Assert.HasCount(2, diagnostics);
        TestContext.WriteLine("Sixteen delayed derived commands with distinct IDs shared one path; an independent command started another.");
    }

    [TestMethod]
    public void APathLimitDoesNotCancelAnotherBranchOfTheSameRoot()
    {
        var diagnostics = new List<TriggerLimitDiagnostic>();
        using var battle = new SignalBattle(new TriggerChainFactory(
            mode: TriggerLimitMode.Production, report: diagnostics.Add));
        var branches = new PathAndSiblingHandler();
        battle.Install(branches, Trigger("again", "cycle"), Trigger("sibling", "other"));

        battle.Signal("again");
        battle.Run();

        Assert.AreEqual(16, branches.CycleCount);
        Assert.AreEqual(1, branches.SiblingCount);
        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(TriggerLimitReason.PathDepth, diagnostics[0].Reason);
        TestContext.WriteLine("The depth-17 cycle branch stopped, while its delayed sibling still executed in the same root.");
    }

    private static TriggerHandler Trigger(string signal, string abilityId) => new()
    {
        Id = "trigger-" + signal, TargetAbilityId = abilityId,
        Config = new AbilityTriggerConfig { Type = TriggerType.Signal, SignalId = signal }
    };

    private static TriggerHandler ProbabilityTrigger(
        string abilityId, string signal, double probability, ITriggerCondition? condition = null) => new()
    {
        Id = "probability-" + abilityId, TargetAbilityId = abilityId,
        Config = new AbilityTriggerConfig
        { Type = TriggerType.Signal, SignalId = signal, Probability = probability,
            Conditions = condition == null ? [] : [condition] }
    };

    private sealed class RejectedCondition : ITriggerCondition
    {
        public bool? Evaluate(TriggerConditionContext context) => false;
    }

    private sealed class SequenceRandom(params double[] values) : Random
    {
        public int Draws { get; private set; }
        public override double NextDouble() => values[Draws++];
    }

    private sealed class InvocationRecorder : IHandler
    {
        public string Id => nameof(InvocationRecorder);
        public List<string> Invocations { get; } = [];

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            Invocations.Add(ev.AbilityId);
            return [];
        }
    }

    private sealed record IntermediateEvent : Event
    {
        public required Unit Unit { get; init; }
        public required string Signal { get; init; }
        public Guid CommandId { get; init; }
    }

    private sealed class EchoHandler : IHandler
    {
        public string Id => nameof(EchoHandler);
        public List<string> Invocations { get; } = [];

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId is not ("A" or "B")) yield break;
            Invocations.Add(ev.AbilityId);
            Unit source = ev.Source ?? throw new InvalidOperationException("The echo requires a source.");
            yield return ev.Context.Factory.NewEvent<IntermediateEvent>() with
            {
                Unit = source, Signal = ev.AbilityId == "A" ? "to-b" : "to-a",
                CommandId = ev.CommandEventId, Receiver = ev.Receiver
            };
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnIntermediate(IntermediateEvent ev)
        {
            yield return ev.Context.Factory.NewEvent<AbilitySignalEvent>() with
            {
                SignalId = ev.Signal, CommandEventId = ev.CommandId,
                Source = ev.Unit, Target = ev.Unit, Receiver = ev.Receiver
            };
        }
    }

    private sealed class ForkHandler(int maximumDepth) : IHandler
    {
        public string Id => nameof(ForkHandler);
        public int Count { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId != "branch") yield break;
            Count++;
            if (ev.TriggerChain.AbilityIds.Count >= maximumDepth) yield break;
            Unit source = ev.Source ?? throw new InvalidOperationException("The branch requires a source.");
            Unit target = ev.Target ?? throw new InvalidOperationException("The branch requires a target.");
            for (int i = 0; i < 2; i++)
                yield return ev.Context.Factory.NewEvent<AbilitySignalEvent>() with
                {
                    SignalId = "fork", CommandEventId = ev.CommandEventId,
                    Source = source, Target = target, Receiver = ev.Receiver
                };
        }
    }

    private sealed class CommandLoopHandler : IHandler
    {
        public string Id => nameof(CommandLoopHandler);
        public int Count { get; private set; }
        public List<Guid> CommandIds { get; } = [];

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId != "command-loop") yield break;
            Unit source = ev.Source ?? throw new InvalidOperationException("The command requires a source.");
            Count++;
            CommandIds.Add(ev.CommandEventId);
            yield return ev.Context.Factory.NewEvent<CommandEvent>() with
            {
                AbilityId = "command-loop", Source = source, Target = ev.Target,
                Receiver = ev.Receiver, TimeSpan = TimeSpan.FromSeconds(1)
            };
        }
    }

    private sealed class PathAndSiblingHandler : IHandler
    {
        public string Id => nameof(PathAndSiblingHandler);
        public int CycleCount { get; private set; }
        public int SiblingCount { get; private set; }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnInvoke(AbilityTriggerInvokeEvent ev)
        {
            if (ev.AbilityId == "other")
            {
                SiblingCount++;
                yield break;
            }
            if (ev.AbilityId != "cycle") yield break;
            Unit source = ev.Source ?? throw new InvalidOperationException("The cycle requires a source.");
            Unit target = ev.Target ?? throw new InvalidOperationException("The cycle requires a target.");
            CycleCount++;
            if (CycleCount == 1)
                yield return ev.Context.Factory.NewEvent<AbilitySignalEvent>() with
                {
                    SignalId = "sibling", CommandEventId = ev.CommandEventId, Source = source,
                    Target = target, Receiver = ev.Receiver, TimeSpan = TimeSpan.FromSeconds(1)
                };
            yield return ev.Context.Factory.NewEvent<AbilitySignalEvent>() with
            {
                SignalId = "again", CommandEventId = ev.CommandEventId,
                Source = source, Target = target, Receiver = ev.Receiver
            };
        }
    }

    private sealed class SignalBattle : IDisposable
    {
        private readonly Context context;
        private readonly Unit unit;
        public TimeSpan CurrentTime => context.ContextTimeSpan;

        public SignalBattle(TriggerChainFactory? factory, Random? random = null)
        {
            context = new Context(causalityFactory: factory, random: random);
            context.Logger.Output = null;
            context.DisableEventHistory();
            unit = new Unit
            {
                Name = "unit", ArchetypeId = "unit", Abilities = new UnitAbilityManager(),
                Property = new UnitProperty { LifePoint = 100, LifePointMax = 100, Level = 1 },
                EventReceiver = context.Factory.NewEventReceiver()
            };
            unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler());
            unit.Initialize();
            context.AddEventReceiver(unit.EventReceiver);
        }

        public void Install(params IHandler[] handlers)
        {
            TestHandlerFactory.Register(unit, handlers);
            context.Dispatch(context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = Guid.NewGuid(), Source = unit, Target = unit,
                AbilityIds = handlers.Select(handler => handler.Id).ToArray(), Receiver = unit.EventReceiver
            }).ToList();
            Run();
        }

        public AbilitySignalEvent Signal(string signal)
        {
            var ev = context.Factory.NewEvent<AbilitySignalEvent>() with
            {
                SignalId = signal, CommandEventId = Guid.NewGuid(),
                Source = unit, Target = unit, Receiver = unit.EventReceiver
            };
            context.Dispatch(ev).ToList();
            return ev;
        }

        public CommandEvent Command(string abilityId)
        {
            var ev = context.Factory.NewEvent<CommandEvent>() with
            { AbilityId = abilityId, Source = unit, Target = unit, Receiver = unit.EventReceiver };
            context.Dispatch(ev).ToList();
            return ev;
        }

        public void Death() => context.Dispatch(context.Factory.NewEvent<DeathResolvedEvent>() with
        { Victim = unit, Receiver = unit.EventReceiver }).ToList();

        public void Run() => Assert.IsFalse(context.RunUntilQueueEmpty().Terminated);
        public void Dispose() => context.Dispose();
    }
}
