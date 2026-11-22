namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class ExperienceRewardAbilityDto
{
    public int? Amount { get; set; }
    public List<string>? VictimTags { get; set; } = [];
    public List<string>? KillerTags { get; set; } = [];
}
