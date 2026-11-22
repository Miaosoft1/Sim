using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class ProjectileAbilityParser : IHandlerParser
{
    public string Type => "Projectile";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        ProjectileAbilityDto data = yaml == null
            ? new ProjectileAbilityDto()
            : context.Deserialize<ProjectileAbilityDto>(yaml)
                ?? throw new InvalidDataException("Projectile data cannot be null.");
        if (data.Speed is double speed && (!double.IsFinite(speed) || speed < 0))
            throw new InvalidDataException("Projectile speed must be finite and non-negative.");
        if ((data.ExplosionTrigger &
             ~(ProjectileExplosionTrigger.OnHit | ProjectileExplosionTrigger.OnArrival)) != 0)
            throw new InvalidDataException("Projectile explosionTrigger contains an unsupported value.");
        double? projectileSpeed = data.Speed;
        TimeSpan delay = AbilityDuration.ParseSeconds(data.DelaySeconds, "delaySeconds", allowZero: true) ?? TimeSpan.Zero;
        AbilityGateDefinition? gate = AbilityGateCompiler.Compile(data.Gate, definition.Id, context);
        TargetSelectorDefinition? selector = TargetSelectorCompiler.Compile(data.Selector);
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
            AbilityHandlerFactory.WithTrigger(new ProjectileAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                FormulaPolicy = policy,
                Labels = AbilityLabel.ActiveCast,
                ProjectileSpeed = projectileSpeed,
                ExplosionTrigger = data.ExplosionTrigger,
                Casting = gate?.Casting,
                Delay = delay,
                Windup = gate?.Windup,
                OccupiesWindup = gate?.OccupiesWindup ?? true,
                GateManager = gate?.Create(),
                Selector = selector?.Create()
            }, definition, gate));
    }
}
