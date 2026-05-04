using Memory.Pipeline.Ingestion;
using Memory.Pipeline.Reflection;
using Memory.Pipeline.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Memory.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IExtractor, LlmExtractor>();
        services.AddScoped<IIngestionPipeline, SimpleIngestionPipeline>();
        services.AddScoped<ISearchPipeline, HybridSearchPipeline>();
        services.AddScoped<IReflectionPipeline, SimpleReflectionPipeline>();
        return services;
    }
}
