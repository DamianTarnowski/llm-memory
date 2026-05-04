namespace Memory.Pipeline.Ingestion;

public sealed record ExtractedNote(
    string Content,
    string ContextDescription,
    List<string> Keywords,
    List<string> Tags);

public sealed record ExtractedEntity(
    string Name,
    string Kind,
    Dictionary<string, string> Attributes);

public sealed record ExtractedRelationship(
    string From,
    string To,
    string Relation,
    Dictionary<string, string> Properties);

public sealed record ExtractionResult(
    ExtractedNote Note,
    List<ExtractedEntity> Entities,
    List<ExtractedRelationship> Relationships);
