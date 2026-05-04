namespace Memory.Pipeline.Search;

public sealed class RerankerOptions
{
    public const string SectionName = "Reranker";

    /// <summary>Set false to skip rerank (HybridSearchPipeline returns RRF order directly).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How many top hybrid candidates to send to the LLM. Higher = better recall, more cost.</summary>
    public int TopN { get; set; } = 20;

    /// <summary>Drop candidates whose LLM relevance falls below this. 0 keeps everything.</summary>
    public double MinRelevance { get; set; } = 0.10;

    /// <summary>
    /// Optional ChatOptions.ModelId override for the rerank call. Useful to use a cheaper/faster
    /// flash model than the default chat model. OpenAI/AzureOpenAI/Anthropic respect this; Vertex
    /// and Bedrock wrappers currently use the model fixed at gateway construction.
    /// </summary>
    public string? ModelOverride { get; set; }

    /// <summary>How many chars per candidate to send to the LLM. Truncates long notes.</summary>
    public int MaxCharsPerCandidate { get; set; } = 600;
}
