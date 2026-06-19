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

        services.AddOptions<GraphRetrievalOptions>()
            .Bind(configuration.GetSection(GraphRetrievalOptions.SectionName));

        services.AddOptions<TimeDecayOptions>()
            .Bind(configuration.GetSection(TimeDecayOptions.SectionName));

        services.AddOptions<QueryExpansionOptions>()
            .Bind(configuration.GetSection(QueryExpansionOptions.SectionName));

        services.AddOptions<QueryRoutingOptions>()
            .Bind(configuration.GetSection(QueryRoutingOptions.SectionName));

        services.AddOptions<SaveFilterOptions>()
            .Bind(configuration.GetSection(SaveFilterOptions.SectionName));

        services.AddOptions<EmbeddingBackfillOptions>()
            .Bind(configuration.GetSection(EmbeddingBackfillOptions.SectionName));

        services.AddOptions<AbstentionOptions>()
            .Bind(configuration.GetSection(AbstentionOptions.SectionName));

        services.AddScoped<IExtractor, LlmExtractor>();
        services.AddScoped<IImportanceJudge, LlmImportanceJudge>();
        services.AddScoped<INoteLinker, LlmNoteLinker>();
        services.AddScoped<IReranker, LlmReranker>();
        services.AddScoped<IGraphRetriever, PprGraphRetriever>();
        services.AddScoped<IQueryExpander, LlmQueryExpander>();
        services.AddScoped<IQueryRouter, LlmQueryRouter>();
        services.AddScoped<IIngestionPipeline, SimpleIngestionPipeline>();
        services.AddScoped<ISearchPipeline, HybridSearchPipeline>();
        services.AddScoped<IReflectionPipeline, SimpleReflectionPipeline>();

        services.AddSingleton<EmbeddingBackfillQueue>();
        services.AddSingleton<IEmbeddingBackfillQueue>(sp => sp.GetRequiredService<EmbeddingBackfillQueue>());
        services.AddHostedService<EmbeddingBackfillService>();
        services.AddHostedService<ReflectionBackgroundService>();

        return services;
    }
}
