using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
