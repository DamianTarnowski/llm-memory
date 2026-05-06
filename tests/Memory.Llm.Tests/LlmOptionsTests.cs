using Memory.Llm;

namespace Memory.Llm.Tests;

/// <summary>
/// LlmOptions defaults are wired into appsettings.json — drift here means
/// silently changing the default model the system runs on. Lock them down.
/// </summary>
public sealed class LlmOptionsTests
{
    [Fact]
    public void Defaults_pin_chat_to_AzureOpenAI_and_embeddings_to_OpenAI()
    {
        var opts = new LlmOptions();

        Assert.Equal(LlmProviderKind.AzureOpenAI, opts.ChatProvider);
        Assert.Equal(LlmProviderKind.OpenAI, opts.EmbeddingProvider);
    }

    [Fact]
    public void Default_embedding_model_is_3072_dim_text_embedding_3_large()
    {
        var opts = new LlmOptions();

        Assert.Equal("text-embedding-3-large", opts.EmbeddingModel);
        Assert.Equal(3072, opts.EmbeddingDimensions);
    }

    [Fact]
    public void Default_chat_model_is_gpt_5_mini()
    {
        // Drift here = silently bumping the default chat model. Keep it explicit;
        // bumping should be a deliberate commit, not an oversight.
        var opts = new LlmOptions();

        Assert.Equal("gpt-5-mini", opts.ChatModel);
    }

    [Fact]
    public void Vertex_image_embedding_defaults_to_disabled_with_us_central_1_region()
    {
        var opts = new LlmOptions();

        Assert.False(opts.GoogleVertex.ImageEmbeddingEnabled);
        Assert.Equal("us-central1", opts.GoogleVertex.ImageEmbeddingRegion);
        // Empty creds → enabling flag still won't construct an embedder; covered
        // separately in LlmServiceCollectionExtensions integration.
    }

    [Fact]
    public void Anthropic_default_chat_model_is_pinned()
    {
        var opts = new LlmOptions();

        Assert.Equal("claude-sonnet-4-6", opts.Anthropic.ChatModelId);
    }

    [Fact]
    public void Bedrock_defaults_to_us_east_1_region()
    {
        var opts = new LlmOptions();

        Assert.Equal("us-east-1", opts.AwsBedrock.Region);
    }

    [Fact]
    public void Vertex_default_location_is_global()
    {
        // gemini-3-* preview models live at location=global; regional locations
        // (us-central1 etc.) return 404. Default must stay global.
        var opts = new LlmOptions();

        Assert.Equal("global", opts.GoogleVertex.Location);
    }
}
