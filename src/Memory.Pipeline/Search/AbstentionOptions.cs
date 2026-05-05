namespace Memory.Pipeline.Search;

public sealed class AbstentionOptions
{
    public const string SectionName = "Abstention";

    /// <summary>
    /// When true, search results with no high-confidence hit set
    /// <see cref="SearchResult.Abstain"/>=true and return empty Hits. Caller is
    /// expected to surface a "no relevant memory" response instead of feeding
    /// weak hits into the agent's context.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum top-hit reranker score considered "confident". When the best
    /// hit's reranker score is below this AND the reranker actually ran,
    /// abstain. Default 0.20 — anything weaker is statistical noise.
    /// </summary>
    public double MinTopScore { get; set; } = 0.20;

    /// <summary>
    /// Minimum top-hit RRF score when the reranker fell back (all candidates
    /// filtered). 0.025 corresponds to roughly two retrievers reinforcing the
    /// same note at rank 1 (2 / (60+1) ≈ 0.033). Below 0.025 means only one
    /// retriever weakly contributed — abstain.
    /// </summary>
    public double MinFusedScore { get; set; } = 0.025;
}
