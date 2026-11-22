using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Ability;

public sealed class ExperienceRewardAbility : AbilityBase
{
    private readonly HashSet<Guid> completedDeaths = [];

    public int Amount { get; init; }
    public IReadOnlyList<string> RequiredVictimTags { get; init; } = [];
    public IReadOnlyList<string> RequiredKillerTags { get; init; } = [];

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnInvoke(AbilityTriggerInvokeEvent ev)
    {
        if (ev.AbilityId != Id ||
            ev.UpstreamEvent is not DeathResolvedEvent death ||
            death.Killer is not { } target ||
            target == death.Victim ||
            !target.IsAlive ||
            RequiredVictimTags.Any(tag => !death.Victim.Tags.Contains(tag)) ||
            RequiredKillerTags.Any(tag => !target.Tags.Contains(tag)) ||
            Amount <= 0 ||
            !completedDeaths.Add(death.DeathId))
            yield break;

        int previous = target.Property.Experience;
        int maximum = Math.Max(0, target.Property.ExperienceMax);
        int experience = (int)Math.Min(maximum, (long)previous + Amount);
        if (experience == previous) yield break;

        target.Property.Experience = experience;
        yield return ev.Context.Factory.NewEvent<UnitExperienceChangedEvent>() with
        {
            Unit = target,
            PreviousExperience = previous,
            Experience = experience,
            ExperienceMax = maximum,
            Receiver = target.EventReceiver,
            TimeSpan = TimeSpan.Zero,
            Priority = ev.Priority
        };
    }
}
