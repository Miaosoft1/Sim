namespace Miaosoft.Sim.Combat.Skill.Bundle
{
    /// <summary>
    /// A bundle of abilities that are installed and uninstalled together, because one game skill
    /// may combine several effects (properties, damage, buffs, ...).
    /// This is an assembly-layer definition distinct from DTO: members reference ability
    /// definitions by id and are materialized through an ability factory at install time.
    /// Defined in the Skill project so TalentTree and Equipment can reference it;
    /// Skill itself only provides the install/uninstall functionality and does not call it.
    /// </summary>
    public class AbilityBundle
    {
        public required string Id { get; set; }
        public required IReadOnlyList<string> AbilityIds { get; set; }
    }
}
