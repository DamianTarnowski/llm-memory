namespace Memory.Pipeline.Search;

public sealed class QueryRoutingOptions
{
    public const string SectionName = "QueryRouting";

    /// <summary>
    /// Enables the LLM router. When disabled, the pipeline preserves the previous
    /// behavior: original query, all retrieval streams, normal reranker settings.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional cheap/fast model override for routing. If empty, uses the default chat model.
    /// </summary>
    public string? ModelOverride { get; set; }

    public int MaxRecentTurns { get; set; } = 6;
    public int MaxVariants { get; set; } = 3;
    public int MaxPromptChars { get; set; } = 6000;
    public int MaxResultsCap { get; set; } = 50;

    /// <summary>
    /// Keep false until a document/blob retriever is implemented. The router can
    /// still identify document-shaped queries, but the pipeline downgrades them
    /// to memory search instead of pretending Blob RAG exists.
    /// </summary>
    public bool AllowDocumentRag { get; set; }
}
