using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Miaosoft.Sim.Combat.Skill.Content.Parser;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public static class SkillContentServiceCollectionExtensions
{
    public const string DamageModuleId = "combat-damage";
    public const string AbilityModuleId = "combat-abilities";

    public static IServiceCollection AddDamageTypeContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DamageTypeParser>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentModule, DamageTypeContentModule>());
        return services;
    }

    public static IServiceCollection AddAbilityContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DamageAbilityParser>();
        services.TryAddSingleton<BeingDamageAbilityParser>();
        services.TryAddSingleton<DeathAbilityParser>();
        services.TryAddSingleton<ProjectileAbilityParser>();
        services.TryAddSingleton<HealAbilityParser>();
        services.TryAddSingleton<StatusAbilityParser>();
        services.TryAddSingleton<PropertyAbilityParser>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentModule, AbilityContentModule>());
        return services;
    }
}
