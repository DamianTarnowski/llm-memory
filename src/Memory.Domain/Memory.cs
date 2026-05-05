namespace Memory.Domain;

public sealed class Episode
{
    public required EpisodeId Id { get; init; }
    public required ProjectId Project { get; init; }
    public required string Source { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed class Note
{
    public required NoteId Id { get; init; }
    public required ProjectId Project { get; init; }
    public EpisodeId? SourceEpisode { get; init; }
    public required string Content { get; init; }
    public required string ContextDescription { get; init; }
    public List<string> Keywords { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public NoteKind Kind { get; init; } = NoteKind.General;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SupersededAt { get; init; }
}

/// <summary>
/// Coarse memory-type ontology — five durable kinds plus a general fallback.
/// Aligns with leading 2026 memory systems (Hindsight, LongMemEval) which
/// consistently surface this taxonomy as a retrieval-quality lever.
/// </summary>
public enum NoteKind
{
    General = 0,
    /// <summary>Factual statement about current state, no decision or action implied.</summary>
    Observation = 1,
    /// <summary>A choice made or position taken, with the reasoning attached.</summary>
    Decision = 2,
    /// <summary>A lesson distilled from experience — "I learned X" / "X turns out to mean Y".</summary>
    Learning = 3,
    /// <summary>A bug, mistake, gotcha, or what-not-to-do for next time.</summary>
    Error = 4,
    /// <summary>A repeating pattern, heuristic, principle, or rule-of-thumb.</summary>
    Pattern = 5,
}

public sealed class ImageEmbedding
{
    public required Guid Id { get; init; }
    public required NoteId NoteId { get; init; }
    public required ProjectId Project { get; init; }
    public required string ModelId { get; init; }
    public required int Dimensions { get; init; }
    public required float[] Embedding { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class NoteEmbedding
{
    public required NoteId NoteId { get; init; }
    public required ProjectId Project { get; init; }
    public required string EmbeddingModel { get; init; }
    public required int Dimensions { get; init; }
    public required float[] Embedding { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class NoteEntityMention
{
    public required NoteId NoteId { get; init; }
    public required EntityId EntityId { get; init; }
    public required ProjectId Project { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class NoteRelation
{
    public required NoteId NoteId { get; init; }
    public required NoteId RelatedNoteId { get; init; }
    public required ProjectId Project { get; init; }
    public required string RelationType { get; init; }
    public required double Confidence { get; init; }
    public required double Similarity { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class ApiKey
{
    public required Guid Id { get; init; }
    public required string KeyHash { get; init; }
    public required OrganizationId Organization { get; init; }
    public required ProjectId Project { get; init; }
    public required UserId CreatedByUser { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class Entity
{
    public required EntityId Id { get; init; }
    public required ProjectId Project { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
}

public sealed class Edge
{
    public required EdgeId Id { get; init; }
    public required ProjectId Project { get; init; }
    public required EntityId From { get; init; }
    public required EntityId To { get; init; }
    public required string Relation { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public DateTimeOffset? InvalidatedAt { get; init; }
    public EpisodeId? SourceEpisode { get; init; }
    public Dictionary<string, string> Properties { get; init; } = new();
}

public sealed class Reflection
{
    public required ReflectionId Id { get; init; }
    public required ProjectId Project { get; init; }
    public required string Scope { get; init; }
    public required string Summary { get; init; }
    public List<NoteId> RelatedNotes { get; init; } = new();
    public List<EntityId> RelatedEntities { get; init; } = new();
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string GeneratorModel { get; init; }
}
