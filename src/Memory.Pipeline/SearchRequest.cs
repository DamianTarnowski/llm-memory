using Memory.Domain;

namespace Memory.Pipeline;

public sealed record SearchRequest(
    string Query,
    int MaxResults = 20,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

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

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCandidates);
