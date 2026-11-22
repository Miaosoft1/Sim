using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class EventPipelineSampleTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SchedulingOrdersWorkAndCancelsScopedFollowUp()
    {
        using var context = new Context();
        context.Logger.Output = null;
        EventReceiver receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        using EventScope canceledScope = context.CreateEventScope();
        var timeline = new List<string>();
        receiver.AddHandler(new Transformer(timeline), new Collector(timeline));

        Enqueue(context, receiver, "first", 2);
        Enqueue(context, receiver, "canceled", 4, scope: canceledScope);
        Enqueue(context, receiver, "urgent", 1, priority: -10);
        Assert.IsFalse(context.RunUntil(TimeSpan.Zero).Terminated);
        CollectionAssert.AreEqual(
            new[] { "0s start urgent", "0s start first", "0s start canceled" }, timeline);

        canceledScope.Cancel();
        Assert.IsFalse(context.RunUntil(TimeSpan.FromMilliseconds(999)).Terminated);
        Assert.HasCount(3, timeline);
        Assert.IsFalse(context.RunUntil(TimeSpan.FromSeconds(1)).Terminated);
        CollectionAssert.AreEqual(new[]
        {
            "0s start urgent", "0s start first", "0s start canceled",
            "1s result urgent=4", "1s result first=5"
        }, timeline);
        TestContext.WriteLine(string.Join(Environment.NewLine, timeline));
    }

    private static void Enqueue(
        Context context, EventReceiver receiver, string name, int value,
        int priority = 0, EventScope? scope = null)
    {
        context.Dispatch(context.Factory.NewEvent<InputEvent>() with
        {
            Receiver = receiver,
            Name = name,
            Value = value,
            Priority = priority,
            Scope = scope
        }).ToList();
    }

    private sealed record InputEvent : Event
    {
        public required string Name { get; init; }
        public int Value { get; init; }
    }

    private sealed record ResultEvent : Event
    {
        public required string Name { get; init; }
        public int Value { get; init; }
    }

    private sealed class Transformer(List<string> timeline) : IHandler
    {
        public string Id => "transformer";

        [Handler]
        public IEnumerable<IEvent> OnInput(InputEvent input)
        {
            timeline.Add($"{input.Context.ContextTimeSpan.TotalSeconds:0}s start {input.Name}");
            yield return input.Context.Factory.NewEvent<ResultEvent>() with
            {
                Receiver = input.Receiver,
                Name = input.Name,
                Value = input.Value + 3,
                Delay = TimeSpan.FromSeconds(1)
            };
        }
    }

    private sealed class Collector(List<string> timeline) : IHandler
    {
        public string Id => "collector";

        [Handler]
        public IEnumerable<IEvent> OnResult(ResultEvent result)
        {
            timeline.Add($"{result.Context.ContextTimeSpan.TotalSeconds:0}s result {result.Name}={result.Value}");
            return [];
        }
    }
}
