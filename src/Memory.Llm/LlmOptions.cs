namespace Memory.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProviderKind ChatProvider { get; set; } = LlmProviderKind.AzureOpenAI;
    public string ChatModel { get; set; } = "gpt-5-mini";
    public LlmProviderKind EmbeddingProvider { get; set; } = LlmProviderKind.OpenAI;
    public string EmbeddingModel { get; set; } = "text-embedding-3-large";
    public int EmbeddingDimensions { get; set; } = 3072;

    public AzureOpenAiSettings AzureOpenAi { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
    public BedrockSettings AwsBedrock { get; set; } = new();
    public VertexSettings GoogleVertex { get; set; } = new();
    public AnthropicSettings Anthropic { get; set; } = new();

    public sealed class AzureOpenAiSettings
    {
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ChatDeployment { get; set; } = string.Empty;
        public string EmbeddingDeployment { get; set; } = string.Empty;
    }

    public sealed class OpenAiSettings
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    public sealed class BedrockSettings
    {
        public string Region { get; set; } = "us-east-1";
        public string ChatModelId { get; set; } = string.Empty;
        public string EmbeddingModelId { get; set; } = string.Empty;
    }

    public sealed class VertexSettings
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Location { get; set; } = "global";
        public string AdcCredentialsPath { get; set; } = string.Empty;
        public string ChatModelId { get; set; } = string.Empty;
        public string EmbeddingModelId { get; set; } = string.Empty;

        /// <summary>
        /// Region for multimodalembedding@001 (the model isn't at "global").
        /// us-central1 is the safe default.
        /// </summary>
        public string ImageEmbeddingRegion { get; set; } = "us-central1";

        /// <summary>
        /// When true and Vertex creds are configured, register VertexImageEmbedder
        /// in DI so the ingestion pipeline stores image_embeddings rows alongside
        /// captions and the search pipeline can fuse image-vector hits.
        /// </summary>
        public bool ImageEmbeddingEnabled { get; set; } = false;
    }

    public sealed class AnthropicSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ChatModelId { get; set; } = "claude-sonnet-4-6";
    }
}
