namespace Miaosoft.Sim.Core;

public record MessageEvent<TMessage> : Event where TMessage : IEventMessage
{
    public TMessage Message { get; init; } = default!;
    public string ActorId { get; init; } = string.Empty;
}

public sealed record MessageAdmissionEvent : MessageEvent<IEventMessage>
{
    public bool? Accepted { get; set; }
    public IEventMessage? Rejection { get; set; }
}

public sealed record MessagePublicationEvent : MessageEvent<IEventMessage>
{
    public string? RecipientId { get; init; }
    public bool SuppressCorrelation { get; init; }
}
