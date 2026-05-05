using Memory.Domain;

namespace Memory.Pipeline;

public sealed record SearchRequest(
    string Query,
    int MaxResults = 20,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    IReadOnlyList<NoteKind>? Kinds = null,
    /// <summary>
    /// When set, hits are packed greedily by descending score until the
    /// estimated token cost of their content exceeds this budget. Useful for
    /// agent integration where the consumer cares about context window, not
    /// row count. Token estimate is conservative (chars / 3.8) — better to
    /// underfill than to blow the model's window.
    /// </summary>
    int? MaxTokens = null);

public sealed record SearchHit(
    NoteId NoteId,
    string Content,
    double Score,
    IReadOnlyList<EntityId> RelatedEntities,
    SearchHitProvenance? Provenance = null);

public sealed record SearchHitProvenance(
    bool FromVector,
    bool FromBm25,
    bool FromGraph,
    double VectorScore,
    double Bm25Score,
    double GraphScore,
    double? RerankerScore);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    int TotalCandidates,
    /// <summary>
    /// True when the pipeline judges no hit relevant enough to surface.
    /// LongMemEval treats abstention ("I don't know") as a core memory ability
    /// — it's better to admit there's no good answer than to surface a weak
    /// one and have the caller / agent confidently misuse it.
    /// </summary>
    bool Abstain = false,
    string? AbstainReason = null);
