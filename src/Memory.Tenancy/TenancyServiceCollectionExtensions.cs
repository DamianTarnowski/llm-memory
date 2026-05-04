using Microsoft.Extensions.DependencyInjection;

namespace Memory.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryTenancy(this IServiceCollection services)
    {
        services.AddSingleton<ITenantContext, AmbientTenantContext>();
        return services;
    }
}
