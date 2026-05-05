using Memory.Domain;

namespace Memory.Pipeline;

public sealed record IngestionRequest(
    string Source,
    string Content,
    DateTimeOffset? OccurredAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record IngestionResult(
    EpisodeId? EpisodeId,
    IReadOnlyList<NoteId> Notes,
    IReadOnlyList<EntityId> EntitiesUpserted,
    bool Skipped = false,
    string? SkipReason = null,
    double? ImportanceScore = null);
