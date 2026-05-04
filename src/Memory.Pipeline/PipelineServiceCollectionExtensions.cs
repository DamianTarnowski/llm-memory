using Memory.Pipeline.Ingestion;
using Memory.Pipeline.Linking;
using Memory.Pipeline.Reflection;
using Memory.Pipeline.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Memory.Pipeline;

public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<LinkingOptions>()
            .Bind(configuration.GetSection(LinkingOptions.SectionName));

        services.AddOptions<ReflectionScheduleOptions>()
            .Bind(configuration.GetSection(ReflectionScheduleOptions.SectionName));

        services.AddOptions<RerankerOptions>()
            .Bind(configuration.GetSection(RerankerOptions.SectionName));

        services.AddScoped<IExtractor, LlmExtractor>();
        services.AddScoped<INoteLinker, LlmNoteLinker>();
        services.AddScoped<IReranker, LlmReranker>();
        services.AddScoped<IIngestionPipeline, SimpleIngestionPipeline>();
        services.AddScoped<ISearchPipeline, HybridSearchPipeline>();
        services.AddScoped<IReflectionPipeline, SimpleReflectionPipeline>();

        services.AddHostedService<ReflectionBackgroundService>();

        return services;
    }
}
