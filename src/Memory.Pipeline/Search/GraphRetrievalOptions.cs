namespace Memory.Pipeline.Search;

public sealed class GraphRetrievalOptions
{
    public const string SectionName = "GraphRetrieval";

    /// <summary>
    /// When true, the search pipeline adds a third candidate stream:
    /// PPR (personalized PageRank) seeded at entities matched from the query,
    /// scored by the cumulative PPR mass on the entities each note mentions.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of PPR iterations. 5-10 is usually plenty for sparse graphs.</summary>
    public int Iterations { get; set; } = 8;

    /// <summary>Restart probability — bigger keeps mass close to seeds, smaller spreads further.</summary>
    public double Alpha { get; set; } = 0.15;

    /// <summary>Cap how many seed entities the LLM-extracted query terms can resolve to.</summary>
    public int MaxSeeds { get; set; } = 8;

    /// <summary>Top-N notes to surface from PPR scoring.</summary>
    public int MaxResults { get; set; } = 30;

    /// <summary>Override model for query-entity extraction. Default: gateway's chat model.</summary>
    public string? QueryExtractorModel { get; set; }
}
