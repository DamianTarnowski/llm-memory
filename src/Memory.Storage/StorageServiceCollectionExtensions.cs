using Memory.Storage.Age;
using Memory.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .PostConfigure(opts =>
            {
                if (string.IsNullOrWhiteSpace(opts.ConnectionString))
                {
                    opts.ConnectionString =
                        configuration.GetConnectionString("memorydb")
                        ?? configuration.GetConnectionString("llm_memory")
                        ?? string.Empty;
                }
            })
            .ValidateOnStart();

        services.AddScoped<TenantConnectionInterceptor>();

        services.AddDbContext<MemoryDbContext>((sp, options) =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            options.UseNpgsql(storage.ConnectionString, npg => npg.UseVector());
            options.AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>());
        });

        services.AddScoped<IGraphContext, AgeGraphContext>();

        return services;
    }
}
