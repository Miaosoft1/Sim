using System.Globalization;
using Miaosoft.Sim.Core;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Miaosoft.Sim.Samples.EventScheduling;

[TestClass]
public sealed class EventSchedulingSample
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void EventQueuePreservesPriorityOrderDelayAndCancellation()
    {
        using var context = new Context();
        context.Logger.Output = null;
        using EventScope scope = context.CreateEventScope();
        EventReceiver receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var trace = new List<string>();
        receiver.AddHandler(new WorkHandler(trace, scope));

        foreach ((string name, int priority) in new[]
            { ("first", -10), ("second", -10), ("normal", 10) })
            context.Dispatch(context.Factory.NewEvent<WorkEvent>() with
            {
                Receiver = receiver, Name = name, Priority = priority
            }).ToList();

        Assert.IsFalse(context.RunUntilQueueEmpty().Terminated);
        CollectionAssert.AreEqual(new[]
        {
            "t=0.0s first", "t=0.0s second", "t=0.0s normal",
            "t=0.5s close-scope", "t=1.0s follow-up"
        }, trace);
        foreach (string line in trace) TestContext.WriteLine(line);
    }

    private sealed record WorkEvent : Event, IPropagateHandlerPriority
    {
        public required string Name { get; init; }
    }

    private sealed class WorkHandler(List<string> trace, EventScope scope) : IHandler
    {
        public string Id => nameof(WorkHandler);

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<IEvent> OnWork(WorkEvent ev)
        {
            trace.Add(string.Create(CultureInfo.InvariantCulture,
                $"t={ev.Context.ContextTimeSpan.TotalSeconds:0.0}s {ev.Name}"));
            if (ev.Name == "close-scope") scope.Cancel();
            if (ev.Name != "first") yield break;

            yield return ev.Context.Factory.NewEvent<WorkEvent>() with
            {
                Receiver = ev.Receiver, Name = "close-scope", Delay = TimeSpan.FromSeconds(0.5)
            };
            yield return ev.Context.Factory.NewEvent<WorkEvent>() with
            {
                Receiver = ev.Receiver, Name = "canceled", Delay = TimeSpan.FromSeconds(1), Scope = scope
            };
            yield return ev.Context.Factory.NewEvent<WorkEvent>() with
            {
                Receiver = ev.Receiver, Name = "follow-up", Delay = TimeSpan.FromSeconds(1)
            };
        }
    }
}
