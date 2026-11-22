namespace Miaosoft.Sim.Combat.Skill.Execution;

internal static class CommittedEventPublisher
{
    public static void Dispatch(Context context, IEnumerable<Event> events, List<Exception> failures)
    {
        foreach (Event next in events)
        {
            try { foreach (IEvent _ in context.Dispatch(next)) { } }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Committed events failed to publish.", failures);
    }
}
