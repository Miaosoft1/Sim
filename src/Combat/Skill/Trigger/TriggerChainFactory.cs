using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill.EventData;
using System.Runtime.CompilerServices;

namespace Miaosoft.Sim.Combat.Skill;

public enum TriggerLimitMode
{
    Strict,
    Production
}

public enum TriggerLimitReason
{
    PathDepth,
    TotalBudget,
    PeriodicDepth,
    ActivePeriodicEffects
}

public sealed record TriggerLimitDiagnostic(
    TriggerLimitReason Reason, string AbilityId, int Depth, int TriggerCount, int Limit)
{
    public int? ActivePeriodicEffects { get; init; }
}

public sealed class TriggerLimitExceededException(TriggerLimitDiagnostic diagnostic)
    : InvalidOperationException(
        $"Ability '{diagnostic.AbilityId}' exceeded the {diagnostic.Reason} limit of {diagnostic.Limit}.")
{
    public TriggerLimitDiagnostic Diagnostic { get; } = diagnostic;
}

public sealed class TriggerChainFactory : IEventCausalityFactory
{
    public const int DefaultTotalBudget = 4096;
    public const int DefaultActivePeriodicEffects = 4096;
    private readonly Action<TriggerLimitDiagnostic>? report;
    private readonly ConditionalWeakTable<Context, PeriodicEffectRegistry> periodicEffects = new();
    private readonly ConditionalWeakTable<Context, AbilityTriggerRouter> routers = new();

    public TriggerChainFactory(
        int totalBudget = DefaultTotalBudget, TriggerLimitMode mode = TriggerLimitMode.Strict,
        Action<TriggerLimitDiagnostic>? report = null,
        int maxActivePeriodicEffects = DefaultActivePeriodicEffects)
    {
        if (totalBudget <= 0) throw new ArgumentOutOfRangeException(nameof(totalBudget));
        if (maxActivePeriodicEffects <= 0) throw new ArgumentOutOfRangeException(nameof(maxActivePeriodicEffects));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == TriggerLimitMode.Production && report == null)
            throw new ArgumentException("Production trigger limits require a diagnostic reporter.", nameof(report));
        TotalBudget = totalBudget;
        MaxActivePeriodicEffects = maxActivePeriodicEffects;
        Mode = mode;
        this.report = report;
    }

    public int TotalBudget { get; }
    public int MaxActivePeriodicEffects { get; }
    public TriggerLimitMode Mode { get; }

    public IEventCausality Create(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AbilityTriggerChain.Create(context, this, GetPeriodicEffects(context), GetRouter(context));
    }

    public int GetActivePeriodicEffectCount(Context context)
    {
        ValidateContext(context);
        return periodicEffects.TryGetValue(context, out PeriodicEffectRegistry? registry)
            ? registry.ActiveCount : 0;
    }

    private PeriodicEffectRegistry GetPeriodicEffects(Context context)
    {
        ValidateContext(context);
        return periodicEffects.GetValue(context, value => new PeriodicEffectRegistry(value, this));
    }

    private AbilityTriggerRouter GetRouter(Context context)
    {
        ValidateContext(context);
        return routers.GetValue(context, value => new AbilityTriggerRouter(value));
    }

    private void ValidateContext(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        if (!context.UsesCausalityFactory(this))
            throw new InvalidOperationException("The trigger factory is not configured for this context.");
    }

    internal void Reject(TriggerLimitDiagnostic diagnostic)
    {
        if (Mode == TriggerLimitMode.Strict)
            throw new TriggerLimitExceededException(diagnostic);
        report!(diagnostic);
    }
}
