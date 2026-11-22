using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Loadout.Content.Inventory.Parser;
using Miaosoft.Sim.Loadout.Content.Talents.Parser;

namespace Miaosoft.Sim.Loadout.Content.Registration;

public static class LoadoutContentServiceCollectionExtensions
{
    public const string InventoryModuleId = "loadout-inventory";
    public const string TalentModuleId = "loadout-talents";
    public const string EquipmentTalentModuleId = "loadout-equipment-talents";

    public static IServiceCollection AddEquipmentTalentContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentModule, EquipmentTalentContentModule>());
        return services;
    }

    public static IServiceCollection AddTalentContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TalentTreeParser>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentModule, TalentContentModule>());
        return services;
    }

    public static IServiceCollection AddInventoryContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ItemParser>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentModule, InventoryContentModule>());
        return services;
    }
}
