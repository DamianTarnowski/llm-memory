using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Memory.Llm;

internal sealed class LlmGateway(IOptions<LlmOptions> options) : ILlmGateway, IDisposable
{
    private readonly LlmOptions _opts = options.Value;
    private readonly Lock _lock = new();
    private IChatClient? _chat;
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private bool _disposed;

    public IChatClient GetChat()
    {
        if (_chat is not null) return _chat;
        lock (_lock)
        {
            return _chat ??= CreateChat();
        }
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddings()
    {
        if (_embeddings is not null) return _embeddings;
        lock (_lock)
        {
            return _embeddings ??= CreateEmbeddings();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_chat as IDisposable)?.Dispose();
        (_embeddings as IDisposable)?.Dispose();
    }

    private IChatClient CreateChat() => _opts.ChatProvider switch
    {
        LlmProviderKind.AzureOpenAI => CreateAzureOpenAiChat(),
        LlmProviderKind.OpenAI => CreateOpenAiChat(),
        LlmProviderKind.AwsBedrock => throw NotYetImplemented("AWS Bedrock chat"),
        LlmProviderKind.GoogleVertex => throw NotYetImplemented("Google Vertex chat"),
        LlmProviderKind.Anthropic => throw NotYetImplemented("Anthropic direct chat"),
        _ => throw new InvalidOperationException($"Unknown chat provider '{_opts.ChatProvider}'."),
    };

    private IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddings() => _opts.EmbeddingProvider switch
    {
        LlmProviderKind.AzureOpenAI => CreateAzureOpenAiEmbeddings(),
        LlmProviderKind.OpenAI => CreateOpenAiEmbeddings(),
        LlmProviderKind.AwsBedrock => throw NotYetImplemented("AWS Bedrock embeddings"),
        LlmProviderKind.GoogleVertex => throw NotYetImplemented("Google Vertex embeddings"),
        LlmProviderKind.Anthropic => throw new NotSupportedException(
            "Anthropic does not provide embedding models — pick a different provider for EmbeddingProvider."),
        _ => throw new InvalidOperationException($"Unknown embedding provider '{_opts.EmbeddingProvider}'."),
    };

    private IChatClient CreateAzureOpenAiChat()
    {
        var s = _opts.AzureOpenAi;
        Require(s.Endpoint, "Llm:AzureOpenAi:Endpoint");
        Require(s.ApiKey, "Llm:AzureOpenAi:ApiKey");
        Require(s.ChatDeployment, "Llm:AzureOpenAi:ChatDeployment");

        var client = new AzureOpenAIClient(new Uri(s.Endpoint), new AzureKeyCredential(s.ApiKey));
        return client.GetChatClient(s.ChatDeployment).AsIChatClient();
    }

    private IChatClient CreateOpenAiChat()
    {
        var s = _opts.OpenAi;
        Require(s.ApiKey, "Llm:OpenAi:ApiKey");

        var client = new OpenAIClient(s.ApiKey);
        return client.GetChatClient(_opts.ChatModel).AsIChatClient();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateAzureOpenAiEmbeddings()
    {
        var s = _opts.AzureOpenAi;
        Require(s.Endpoint, "Llm:AzureOpenAi:Endpoint");
        Require(s.ApiKey, "Llm:AzureOpenAi:ApiKey");
        Require(s.EmbeddingDeployment, "Llm:AzureOpenAi:EmbeddingDeployment");

        var client = new AzureOpenAIClient(new Uri(s.Endpoint), new AzureKeyCredential(s.ApiKey));
        return client.GetEmbeddingClient(s.EmbeddingDeployment).AsIEmbeddingGenerator();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOpenAiEmbeddings()
    {
        var s = _opts.OpenAi;
        Require(s.ApiKey, "Llm:OpenAi:ApiKey");

        var client = new OpenAIClient(s.ApiKey);
        return client.GetEmbeddingClient(_opts.EmbeddingModel).AsIEmbeddingGenerator();
    }

    private static void Require(string value, string configKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"LLM configuration is missing '{configKey}'. Set it in appsettings, user-secrets, or environment variables.");
        }
    }

    private static NotImplementedException NotYetImplemented(string what) =>
        new($"{what} provider is not yet implemented in LlmGateway. Wire it up via Microsoft.Extensions.AI in src/Memory.Llm/LlmGateway.cs.");
}
