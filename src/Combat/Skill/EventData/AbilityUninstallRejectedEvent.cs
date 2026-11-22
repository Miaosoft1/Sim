namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class AbilityUninstallRejectedEvent : Event
    {
        public required Guid CommandEventId { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public required IReadOnlyList<string> ProtectedAbilityIds { get; set; }
    }
}
