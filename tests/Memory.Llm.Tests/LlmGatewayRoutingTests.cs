using Memory.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.Llm.Tests;

/// <summary>
/// Configuration-validation tests for the multi-provider gateway. None of
/// these hit a real provider — they exercise the routing dispatch and the
/// "missing required setting" error messages that surface to operators.
///
/// Anthropic embeddings + Vertex/Bedrock embeddings are intentionally
/// rejected at construction with a documented message; it's the gateway's
/// job to fail loudly instead of letting an unconfigured request 500 out
/// inside a real network call.
/// </summary>
public sealed class LlmGatewayRoutingTests
{
    [Fact]
    public void GetEmbeddings_throws_NotSupported_for_Anthropic()
    {
        var sp = BuildServices(o =>
        {
            o.EmbeddingProvider = LlmProviderKind.Anthropic;
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var ex = Assert.Throws<NotSupportedException>(() => gw.GetEmbeddings());
        Assert.Contains("Anthropic does not provide embedding models", ex.Message);
    }

    [Fact]
    public void GetEmbeddings_throws_NotImplemented_for_Bedrock()
    {
        var sp = BuildServices(o =>
        {
            o.EmbeddingProvider = LlmProviderKind.AwsBedrock;
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var ex = Assert.Throws<NotImplementedException>(() => gw.GetEmbeddings());
        Assert.Contains("AWS Bedrock embedding provider is not yet wired", ex.Message);
    }

    [Fact]
    public void GetEmbeddings_throws_NotImplemented_for_Vertex()
    {
        var sp = BuildServices(o =>
        {
            o.EmbeddingProvider = LlmProviderKind.GoogleVertex;
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var ex = Assert.Throws<NotImplementedException>(() => gw.GetEmbeddings());
        Assert.Contains("Google Vertex embedding provider is not yet wired", ex.Message);
    }

    [Fact]
    public void GetChat_AzureOpenAI_without_endpoint_surfaces_named_setting()
    {
        var sp = BuildServices(o =>
        {
            o.ChatProvider = LlmProviderKind.AzureOpenAI;
            // AzureOpenAi.{Endpoint,ApiKey,ChatDeployment} all empty by default.
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var ex = Assert.ThrowsAny<Exception>(() => gw.GetChat());
        Assert.Contains("Llm:AzureOpenAi:Endpoint", ex.Message);
    }

    [Fact]
    public void GetChat_OpenAI_without_apikey_surfaces_named_setting()
    {
        var sp = BuildServices(o =>
        {
            o.ChatProvider = LlmProviderKind.OpenAI;
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var ex = Assert.ThrowsAny<Exception>(() => gw.GetChat());
        Assert.Contains("Llm:OpenAi:ApiKey", ex.Message);
    }

    [Fact]
    public void GetChat_Anthropic_without_apikey_surfaces_named_setting()
    {
        var sp = BuildServices(o =>
        {
            o.ChatProvider = LlmProviderKind.Anthropic;
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var ex = Assert.ThrowsAny<Exception>(() => gw.GetChat());
        Assert.Contains("Llm:Anthropic:ApiKey", ex.Message);
    }

    [Fact]
    public void Gateway_caches_embeddings_client_across_calls()
    {
        // OpenAI client construction needs an API key but the routing decision
        // happens before that. Use a minimal valid OpenAI config so the
        // generator is constructable, then assert the gateway returns the same
        // instance both times (no per-call construction).
        var sp = BuildServices(o =>
        {
            o.EmbeddingProvider = LlmProviderKind.OpenAI;
            o.OpenAi.ApiKey = "sk-test-not-real";
        });
        var gw = sp.GetRequiredService<ILlmGateway>();

        var a = gw.GetEmbeddings();
        var b = gw.GetEmbeddings();

        Assert.Same(a, b);
    }

    private static IServiceProvider BuildServices(Action<LlmOptions> configure)
    {
        // The DI extension reads from IConfiguration; we bind an empty config and
        // then PostConfigure to apply the test-specific overrides.
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddMemoryLlm(config);
        services.PostConfigure<LlmOptions>(configure);
        return services.BuildServiceProvider();
    }
}
