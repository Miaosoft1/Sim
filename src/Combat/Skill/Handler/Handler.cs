using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Handler
{
    public abstract class Handler : IHandler
    {
        public virtual string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public virtual int Priority { get; set; }
    }
}