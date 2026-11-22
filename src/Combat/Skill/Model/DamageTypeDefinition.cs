using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill
{
    /// <summary>
    /// Definition of a damage type declared in configuration.
    /// Includes damage type identifier, display metadata, and mitigation formula.
    /// Code does not hardcode any damage types.
    /// </summary>
    public sealed class DamageTypeDefinition
    {
        public string Id { get; }
        public string? Name { get; }
        public string? Description { get; }
        public NumericFormula<CombatFormulaContext>? Formula { get; }

        public DamageTypeDefinition(
            string id, NumericFormula<CombatFormulaContext>? formula = null, string? name = null, string? description = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Damage type id cannot be null or empty.", nameof(id));
            }

            Id = id.Trim();
            Name = name;
            Description = description;
            Formula = formula;
        }
    }

    /// <summary>
    /// Registry of all valid damage types declared in configuration.
    /// Validates that skill damage definitions do not exceed the declared scope.
    /// </summary>
    public sealed class DamageTypeRegistry
    {
        private readonly Dictionary<string, DamageTypeDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyCollection<DamageTypeDefinition> all;

        public DamageTypeRegistry(IEnumerable<DamageTypeDefinition> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);
            foreach (DamageTypeDefinition definition in definitions)
            {
                ArgumentNullException.ThrowIfNull(definition);
                if (!this.definitions.TryAdd(definition.Id, definition))
                    throw new InvalidOperationException($"Damage type '{definition.Id}' is already defined.");
            }
            all = Array.AsReadOnly(this.definitions.Values.ToArray());
        }

        public bool IsDefined(string damageType)
        {
            if (string.IsNullOrWhiteSpace(damageType)) return false;
            return definitions.ContainsKey(damageType.Trim());
        }

        public DamageTypeDefinition? Get(string damageType)
        {
            if (string.IsNullOrWhiteSpace(damageType)) return null;
            return definitions.TryGetValue(damageType.Trim(), out var def) ? def : null;
        }

        public DamageTypeDefinition Require(string damageType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(damageType);
            return Get(damageType)
                ?? throw new ArgumentException($"Damage type '{damageType}' is not declared.", nameof(damageType));
        }

        public IReadOnlyCollection<DamageTypeDefinition> All => all;
    }
}
