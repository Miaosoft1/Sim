namespace Miaosoft.Sim.Core;

/// <summary>Thread-safe ingress for a context's single execution owner.</summary>
public sealed class ContextEventInbox
{
    internal abstract record Work;
    internal sealed record PostedEvent(IEvent Event) : Work;
    internal sealed record RejectedEvent(IEvent Event) : Work;
    internal sealed record RejectedRequest(string Name) : Work;
    internal sealed record StopRequest : Work;
    internal sealed record TimeScaleRequest(double Value) : Work;

    private readonly object sync = new();
    private readonly Queue<Work> pending = [];
    private TaskCompletionSource wake = NewWakeSource();
    private Func<IEvent, bool>? admission;
    private bool signaled;
    private bool completed;

    public event Action? EventPosted;
    public event Action? ControlRequestPosted;

    /// <summary>
    /// Transfers an immediate event to the owner, which assigns its creation time.
    /// The caller must not mutate the event after posting it.
    /// </summary>
    public void Post(IEvent ev)
    {
        if (!TryPost(ev))
            throw new InvalidOperationException("The context inbox has been completed.");
    }

    /// <summary>Posts an event unless the owning context has finished and closed its inbox.</summary>
    public bool TryPost(IEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!TryEnqueue(new PostedEvent(ev))) return false;
        EventPosted?.Invoke();
        return true;
    }

    public void RequestStop()
    {
        if (!TryEnqueue(new StopRequest()))
            throw new InvalidOperationException("The context inbox has been completed.");
        ControlRequestPosted?.Invoke();
    }

    public void RequestTimeScale(double value)
    {
        Context.ValidateTimeScale(value);
        if (!TryEnqueue(new TimeScaleRequest(value)))
            throw new InvalidOperationException("The context inbox has been completed.");
        ControlRequestPosted?.Invoke();
    }

    /// <summary>Restricts subsequent inbox work after the owner has finished normal execution.</summary>
    public void RestrictPostedEvents(Func<IEvent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (sync)
        {
            if (completed) throw new InvalidOperationException("The context inbox has been completed.");
            if (admission != null)
                throw new InvalidOperationException("The inbox is already restricted.");
            admission = predicate;
            int count = pending.Count;
            for (int i = 0; i < count; i++) pending.Enqueue(Admit(pending.Dequeue()));
        }
    }

    /// <summary>Wakes the owner to admit work held outside this inbox.</summary>
    public void Signal()
    {
        lock (sync) SignalCore();
    }

    internal bool HasPending
    {
        get { lock (sync) return pending.Count != 0; }
    }

    internal Task WakeTask
    {
        get { lock (sync) return wake.Task; }
    }

    internal bool TryDequeue(out Work? work)
    {
        lock (sync) return pending.TryDequeue(out work);
    }

    internal bool ConsumeSignal()
    {
        lock (sync)
        {
            if (!signaled) return false;
            ResetSignal();
            return true;
        }
    }

    internal void Clear()
    {
        lock (sync)
        {
            pending.Clear();
            if (signaled) ResetSignal();
        }
    }

    internal void Complete()
    {
        lock (sync)
        {
            if (completed) return;
            completed = true;
            pending.Clear();
            admission = null;
            EventPosted = null;
            ControlRequestPosted = null;
            SignalCore();
        }
    }

    private bool TryEnqueue(Work work)
    {
        lock (sync)
        {
            if (completed) return false;
            pending.Enqueue(Admit(work));
            SignalCore();
            return true;
        }
    }

    private Work Admit(Work work) => admission == null ? work : work switch
    {
        PostedEvent posted when !admission(posted.Event) => new RejectedEvent(posted.Event),
        StopRequest => new RejectedRequest("stop"),
        TimeScaleRequest => new RejectedRequest("time scale"),
        _ => work
    };

    private void SignalCore()
    {
        if (signaled) return;
        signaled = true;
        wake.SetResult();
    }

    private void ResetSignal()
    {
        wake = NewWakeSource();
        signaled = false;
    }

    private static TaskCompletionSource NewWakeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
