using Miaosoft.Sim.Combat.Skill;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class DamageFailureEvent : Event
    {
        public required DamageEvent Damage { get; set; }
        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(Damage)} = {Damage.DamageAbilityId ?? "<null>"}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}

