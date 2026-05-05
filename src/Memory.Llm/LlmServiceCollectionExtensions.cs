using Memory.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.Llm;

public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryLlm(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<ILlmGateway, LlmGateway>();
        services.AddScoped<IImageDescriber, LlmImageDescriber>();

        services.AddSingleton<ImageEmbedderHolder>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            var v = opts.GoogleVertex;
            if (!v.ImageEmbeddingEnabled
                || string.IsNullOrWhiteSpace(v.ProjectId)
                || string.IsNullOrWhiteSpace(v.AdcCredentialsPath))
            {
                return new ImageEmbedderHolder(null);
            }
            var tokens = new VertexAccessTokenProvider(v.AdcCredentialsPath);
            return new ImageEmbedderHolder(new VertexImageEmbedder(tokens, v.ProjectId, v.ImageEmbeddingRegion));
        });

        return services;
    }
}
