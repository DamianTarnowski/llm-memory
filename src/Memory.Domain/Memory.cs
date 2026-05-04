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
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SupersededAt { get; init; }
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
