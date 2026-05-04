namespace Memory.Pipeline.Search;

public sealed class QueryExpansionOptions
{
    public const string SectionName = "QueryExpansion";

    /// <summary>
    /// When true, short / vague queries get expanded into 2-3 variants by the LLM. Each
    /// variant is embedded and vector-searched; results are RRF-fused into a single
    /// vector-stream before joining BM25 and graph in the main pipeline. Boosts recall
    /// on terse queries like "AGE" or "deploy" where the original embedding is too
    /// thin to retrieve well.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Only expand when the query has at most this many whitespace-separated words.</summary>
    public int MaxQueryWords { get; set; } = 4;

    /// <summary>Number of variants the LLM should generate (in addition to the original).</summary>
    public int VariantCount { get; set; } = 3;

    /// <summary>Override model for variant generation. Defaults to the gateway's chat model.</summary>
    public string? ModelOverride { get; set; }
}
