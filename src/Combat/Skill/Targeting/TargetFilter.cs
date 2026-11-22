namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    public enum TargetFilter
    {
        All = 0,
        Enemy = 1,
        Ally = 2,
        Self = 3
    }

    public static class TargetFilterExtensions
    {
        public static bool Matches(this TargetFilter filter, Unit source, Unit candidate)
        {
            return filter switch
            {
                TargetFilter.All => true,
                TargetFilter.Enemy => candidate != source && source.TeamId != candidate.TeamId,
                TargetFilter.Ally => source.TeamId == candidate.TeamId,
                TargetFilter.Self => candidate == source,
                _ => true
            };
        }
    }
}
