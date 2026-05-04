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
    IReadOnlyList<EntityId> RelatedEntities);

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCandidates);
