using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Parser;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

internal sealed class DamageTypeContentModule : IContentModule
{
    private readonly DamageTypeParser parser;

    public DamageTypeContentModule(DamageTypeParser parser) => this.parser = parser;

    public string Id => SkillContentServiceCollectionExtensions.DamageModuleId;

    public void Register(ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ExpressionSchema<CombatFormulaContext> schema = registry.CombatFormulas();
        schema.RegisterNumber("raw", context => context.Raw, nullable: true);
        schema.RegisterNumber("ability.level", context => context.AbilityLevel);
        schema.RegisterNumber("source.level", context => context.SourceProperty?.Level, nullable: true);
        schema.RegisterNumber("target.level", context => context.TargetProperty?.Level, nullable: true);
        var authored = new Dictionary<string, (DamageTypeDto Dto, string Source)>(StringComparer.OrdinalIgnoreCase);
        registry.RegisterReferenceResolver("damageType", id =>
            registry.Get<DamageTypeRegistry>("damageType").IsDefined(id));
        registry.RegisterCollection<DamageTypeRegistry>("damageType", (yaml, context) =>
        {
            List<DamageTypeDto> entries = context.Deserialize<List<DamageTypeDto>>(yaml)
                ?? throw new InvalidDataException($"{context.SourceFile}: damageType section must be a list.");
            foreach (DamageTypeDto dto in entries)
            {
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id))
                    throw new InvalidDataException($"{context.SourceFile}: damage type requires an ID.");
                string id = dto.Id.Trim();
                if (!authored.TryAdd(id, (dto, context.SourceFile)))
                    throw new InvalidDataException($"{context.SourceFile}: duplicate damage type '{id}'.");
            }
        }, () =>
        {
            var compiled = new List<DamageTypeDefinition>(authored.Count);
            foreach ((string id, (DamageTypeDto dto, string source)) in authored)
            {
                try { compiled.Add(parser.Compile(dto, schema)); }
                catch (Exception exception) when (exception is FormatException or ArgumentException)
                {
                    throw new InvalidDataException($"{source}: damage type '{id}': {exception.Message}", exception);
                }
            }
            return new DamageTypeRegistry(compiled);
        });
    }
}
