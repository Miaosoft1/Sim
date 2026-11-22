using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed class AbilityTriggerChain : IEventCausality
{
    public const int MaximumDepth = 16;

    private sealed class Budget(TriggerChainFactory factory)
    {
        public TriggerChainFactory Factory { get; } = factory;
        public int Used { get; set; }
        public bool ExhaustionReported { get; set; }
    }

    private readonly Budget budget;
    internal PeriodicEffectRegistry PeriodicEffects { get; }
    internal AbilityTriggerRouter Router { get; }

    private AbilityTriggerChain(
        Context context, Budget budget, IReadOnlyList<string> abilityIds, int periodicDepth,
        PeriodicEffectRegistry periodicEffects, AbilityTriggerRouter router)
    {
        Context = context;
        this.budget = budget;
        AbilityIds = abilityIds;
        PeriodicDepth = periodicDepth;
        PeriodicEffects = periodicEffects;
        Router = router;
    }

    public Context Context { get; }
    public IReadOnlyList<string> AbilityIds { get; }
    public int PeriodicDepth { get; }
    public int TriggerCount => budget.Used;

    internal static AbilityTriggerChain Create(
        Context context, TriggerChainFactory factory, PeriodicEffectRegistry periodicEffects,
        AbilityTriggerRouter router) =>
        new(context, new Budget(factory), Array.Empty<string>(), 0, periodicEffects, router);

    public AbilityTriggerChain? Append(string abilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abilityId);
        if (budget.Used >= budget.Factory.TotalBudget)
        {
            if (!budget.ExhaustionReported || budget.Factory.Mode == TriggerLimitMode.Strict)
            {
                budget.Factory.Reject(new(TriggerLimitReason.TotalBudget, abilityId,
                    AbilityIds.Count, budget.Used, budget.Factory.TotalBudget));
                budget.ExhaustionReported = true;
            }
            return null;
        }
        if (AbilityIds.Count >= MaximumDepth)
        {
            budget.Factory.Reject(new(TriggerLimitReason.PathDepth, abilityId,
                AbilityIds.Count, budget.Used, MaximumDepth));
            return null;
        }

        budget.Used++;
        return new AbilityTriggerChain(
            Context, budget, Array.AsReadOnly(AbilityIds.Append(abilityId).ToArray()), PeriodicDepth, PeriodicEffects, Router);
    }

    internal AbilityTriggerChain? EnterPeriodic(string abilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abilityId);
        if (PeriodicDepth >= MaximumDepth)
        {
            budget.Factory.Reject(new(TriggerLimitReason.PeriodicDepth, abilityId,
                PeriodicDepth, budget.Used, MaximumDepth));
            return null;
        }
        return new AbilityTriggerChain(Context, budget, AbilityIds, PeriodicDepth + 1, PeriodicEffects, Router);
    }

    internal AbilityTriggerChain RestartPeriodicTick() =>
        new(Context, new Budget(budget.Factory), Array.Empty<string>(), PeriodicDepth, PeriodicEffects, Router);
}
