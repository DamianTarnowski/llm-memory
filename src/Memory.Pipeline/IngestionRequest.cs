using Memory.Domain;

namespace Memory.Pipeline;

public sealed record IngestionRequest(
    string Source,
    string Content,
    DateTimeOffset? OccurredAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool ForceSave = false,
    MemoryType? MemoryTypeOverride = null,
    NoteKind? NoteKindOverride = null,
    bool DirectNote = false,
    bool DeferEmbedding = false);

public sealed record IngestionResult(
    EpisodeId? EpisodeId,
    IReadOnlyList<NoteId> Notes,
    IReadOnlyList<EntityId> EntitiesUpserted,
    bool Skipped = false,
    string? SkipReason = null,
    double? ImportanceScore = null);
