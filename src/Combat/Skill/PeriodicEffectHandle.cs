using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class PeriodicEffectHandle
{
    private readonly CancellationTokenRegistration upstreamCancellation;
    private readonly StatusAbility? stackOwner;

    internal PeriodicEffectHandle(
        PeriodicEffectRegistry registry, Unit target, AbilityTriggerChain chain,
        EventCancellation cancellation, TimeSpan? expiresAt, Guid? statusApplicationId = null,
        StatusApplicationOrigin? applicationOrigin = null, StatusAbility? stackOwner = null)
    {
        Registry = registry;
        Target = target;
        Chain = chain;
        CreatedAt = chain.Context.ContextTimeSpan;
        ExpiresAt = expiresAt;
        StatusApplicationId = statusApplicationId;
        ApplicationOrigin = applicationOrigin;
        this.stackOwner = stackOwner;
        Cancellation = new CancellationTokenSource();
        upstreamCancellation = cancellation.Register(() =>
        {
            try { Cancellation.Cancel(); }
            finally { Registry.NotifyCanceled(this); }
        });
    }

    public Guid Id { get; } = Guid.NewGuid();
    internal PeriodicEffectRegistry Registry { get; }
    internal Unit Target { get; }
    internal AbilityTriggerChain Chain { get; }
    internal TimeSpan CreatedAt { get; }
    internal TimeSpan? ExpiresAt { get; }
    internal Guid? StatusApplicationId { get; }
    public StatusApplicationOrigin? ApplicationOrigin { get; }
    internal CancellationTokenSource Cancellation { get; }

    internal int ReadStatusStack()
    {
        if (stackOwner == null) return 1;
        if (StatusApplicationId == null || stackOwner.ApplicationId != StatusApplicationId)
            throw new InvalidOperationException("The periodic effect does not match its status identity.");
        int stack = stackOwner.CurrentStack;
        return stack > 0 ? stack :
            throw new InvalidOperationException("A periodic status requires a positive stack count.");
    }

    internal void Close(bool cancel)
    {
        upstreamCancellation.Unregister();
        if (cancel)
            Cancellation.Cancel();
    }
}
