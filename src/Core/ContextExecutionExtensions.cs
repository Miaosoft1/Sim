namespace Miaosoft.Sim.Core;

public sealed record ContextRunOptions
{
    public TimeSpan? UntilTime { get; init; }
    public int? MaxEvents { get; init; }
    public Func<IEvent, bool>? StopWhen { get; init; }
    public bool ExitWhenQueueEmpty { get; init; }
}

public sealed record ContextRunResult(
    ExitEvent ExitEvent,
    IEvent? LastEvent,
    TimeSpan CurrentTime,
    int ProcessedCount)
{
    public StopReason Reason => ExitEvent.Reason;
    public bool Terminated { get; init; }
}

public static class ContextExecutionExtensions
{
    public static ContextRunResult Run(this Context context, ContextRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureSynchronousExecutionAllowed();
        options ??= new ContextRunOptions();
        int processed = 0;
        IEvent? lastEvent = null;

        while (context.HasPendingEvents)
        {
            IEvent? next = context.PeekNext();
            if (options.UntilTime.HasValue && next != null
                && next.Create.Add(next.Delay) > options.UntilTime.Value)
            {
                return Result(context, StopReason.TimeLimitReached, lastEvent, processed);
            }

            bool keepRunning = context.Next(out IEvent? current);
            if (!keepRunning)
            {
                if (current is not ExitEvent exit)
                {
                    throw new InvalidOperationException("A terminating Context step must return an ExitEvent.");
                }

                return new ContextRunResult(exit, lastEvent, context.ContextTimeSpan, processed)
                {
                    Terminated = true
                };
            }

            if (current == null)
            {
                continue;
            }

            processed++;
            lastEvent = current;
            if (options.MaxEvents.HasValue && processed >= options.MaxEvents.Value)
            {
                return Result(context, StopReason.EventLimitReached, lastEvent, processed);
            }

            if (options.StopWhen != null && options.StopWhen(current))
            {
                return Result(context, StopReason.StopRequested, lastEvent, processed);
            }
        }

        return Result(context, StopReason.QueueEmpty, lastEvent, processed);
    }

    public static ContextRunResult RunUntil(this Context context, TimeSpan until) =>
        context.Run(new ContextRunOptions { UntilTime = until });

    public static void AdvanceTo(this Context context, TimeSpan until)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (until < context.ContextTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(until), "Cannot move simulation time backwards.");
        }

        ContextRunResult result = context.RunUntil(until);
        if (result.Terminated)
        {
            throw new InvalidOperationException($"Context terminated while advancing: {result.Reason}.");
        }

        context.SetTime(until);
    }

    public static ContextRunResult RunUntilQueueEmpty(this Context context) =>
        context.Run(new ContextRunOptions { ExitWhenQueueEmpty = true });

    public static ContextRunResult RunUntil(this Context context, Func<IEvent, bool> stopWhen) =>
        context.Run(new ContextRunOptions { StopWhen = stopWhen });

    public static ContextRunResult RunFor(this Context context, int maxEvents) =>
        context.Run(new ContextRunOptions { MaxEvents = maxEvents });

    private static ContextRunResult Result(Context context, StopReason reason, IEvent? lastEvent, int processed) =>
        new(new ExitEvent { Create = context.ContextTimeSpan, Delay = TimeSpan.Zero, Reason = reason },
            lastEvent, context.ContextTimeSpan, processed);
}
