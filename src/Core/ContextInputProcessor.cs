namespace Miaosoft.Sim.Core;

public sealed class ContextInputProcessor(Context context, EventReceiver receiver)
{
    public void Process(IEnumerable<BufferedEventMessage> input)
    {
        foreach (IEvent ev in CreateEvents(input))
        {
            context.Dispatch(ev).ToList();
            ContextRunResult result = context.RunUntil(context.ContextTimeSpan);
            if (result.Terminated)
            {
                throw new InvalidOperationException($"Context terminated while processing input: {result.Reason}.");
            }
        }
    }

    public IEnumerable<IEvent> CreateEvents(IEnumerable<BufferedEventMessage> input)
    {
        var buffer = new EventMessageBuffer();
        BufferedEventMessage? previous = null;
        foreach (var entry in input)
        {
            EventMessageBuffer.Validate(entry.Message);
            if (previous != null && !EventMessageBuffer.CanMerge(previous, entry))
            {
                foreach (IEvent pending in Flush(buffer)) yield return pending;
                previous = null;
            }

            var admission = context.Factory.NewEvent<MessageAdmissionEvent>() with
            {
                ActorId = entry.ActorId, Message = entry.Message, Receiver = receiver,
                CorrelationId = entry.CorrelationId, TimeSpan = TimeSpan.Zero
            };
            yield return admission;
            if (admission.Accepted == null)
                throw new InvalidOperationException("No Context handler admitted the input message.");
            if (!admission.Accepted.Value)
            {
                foreach (IEvent pending in Flush(buffer)) yield return pending;
                previous = null;
                if (admission.Rejection == null)
                    throw new InvalidOperationException("Rejected input must provide explicit feedback.");
                yield return context.Factory.NewEvent<MessagePublicationEvent>() with
                {
                    Message = admission.Rejection, RecipientId = entry.ActorId, Receiver = receiver,
                    CorrelationId = entry.CorrelationId, TimeSpan = TimeSpan.Zero
                };
                continue;
            }

            buffer.Enqueue(entry.ActorId, entry.Message, entry.CorrelationId);
            previous = entry;
            if (entry.Message is IDiscreteEvent)
            {
                foreach (IEvent pending in Flush(buffer)) yield return pending;
                previous = null;
            }
        }
        foreach (IEvent pending in Flush(buffer)) yield return pending;
    }

    private IEnumerable<IEvent> Flush(EventMessageBuffer buffer)
    {
        foreach (var entry in buffer.Drain())
            yield return context.Factory.NewEvent<MessageEvent<IEventMessage>>() with
            {
                ActorId = entry.ActorId, Message = entry.Message, Receiver = receiver,
                CorrelationId = entry.CorrelationId, TimeSpan = TimeSpan.Zero
            };
    }
}
