using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler
{
    public class CommandDamageHandler : Handler
    {
        public CommandDamageHandler()
        {
            Id = nameof(CommandDamageHandler);
            Priority = (int)DamageChainStage.Command;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnCommand(CommandEvent ev)
        {
            yield break;
        }
    }
}
