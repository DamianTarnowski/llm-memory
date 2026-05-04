using System.ComponentModel;
using Memory.Domain;
using Memory.Pipeline;
using Memory.Pipeline.Reflection;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

[McpServerToolType]
public static class MemoryTools
{
    [McpServerTool(Name = "save_episode")]
    [Description("Save a raw episode (chat turn, document chunk, observation) to the active project's memory. " +
                 "Returns the episode id and any notes/entities extracted.")]
    public static async Task<SaveEpisodeResponse> SaveEpisodeAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("Logical source label, e.g. 'chat', 'doc', 'observation'.")] string source,
        [Description("The full text content of the episode.")] string content,
        [Description("Optional ISO 8601 timestamp of when this episode actually occurred (defaults to now).")] string? occurredAt = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        DateTimeOffset? when = null;
        if (!string.IsNullOrWhiteSpace(occurredAt) && DateTimeOffset.TryParse(occurredAt, out var parsed))
        {
            when = parsed;
        }

        var result = await pipeline.IngestAsync(new IngestionRequest(source, content, when), ct);
        return new SaveEpisodeResponse(
            result.EpisodeId.ToString(),
            result.Notes.Select(n => n.ToString()).ToArray(),
            result.EntitiesUpserted.Select(e => e.ToString()).ToArray());
    }

    [McpServerTool(Name = "search_memory")]
    [Description("Hybrid search across the active project's memory (vector + graph + temporal). " +
                 "Returns ranked notes with their related entities.")]
    public static async Task<SearchMemoryResponse> SearchMemoryAsync(
        ISearchPipeline pipeline,
        ITenantContext tenant,
        [Description("Natural language query.")] string query,
        [Description("Maximum number of hits to return (default 20).")] int maxResults = 20,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        var result = await pipeline.SearchAsync(new SearchRequest(query, maxResults), ct);
        return new SearchMemoryResponse(
            result.Hits.Select(h => new SearchMemoryHit(
                h.NoteId.ToString(),
                h.Content,
                h.Score,
                h.RelatedEntities.Select(e => e.ToString()).ToArray())).ToArray(),
            result.TotalCandidates);
    }

    [McpServerTool(Name = "reflect")]
    [Description("Synthesize recent notes into a reflection (key themes, tensions, actionable insights). " +
                 "Stored as a Reflection record and returned as a summary string.")]
    public static async Task<ReflectMemoryResponse> ReflectAsync(
        IReflectionPipeline pipeline,
        ITenantContext tenant,
        [Description("Logical scope label, e.g. 'recent', 'weekly', 'project-X'.")] string scope = "recent",
        [Description("Maximum number of notes to consider (default 30).")] int maxNotes = 30,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        var result = await pipeline.ReflectAsync(new ReflectionRequest(scope, maxNotes), ct);
        return new ReflectMemoryResponse(
            result.Id.ToString(),
            result.Scope,
            result.Summary,
            result.NotesConsidered);
    }

    [McpServerTool(Name = "find_related_notes")]
    [Description("Return note-to-note relations (auto-linked at ingest time via A-MEM). " +
                 "Useful for exploring conceptual neighbours, contradictions, or specializations of a known note.")]
    public static async Task<FindRelatedNotesResponse> FindRelatedNotesAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        [Description("Note id (uuid) of the source note.")] string noteId,
        [Description("Maximum number of relations to return (default 25).")] int maxResults = 25,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        if (!Guid.TryParse(noteId, out var guid))
        {
            return new FindRelatedNotesResponse(Array.Empty<RelatedNoteView>());
        }

        var nid = new NoteId(guid);
        var rows = await db.NoteRelations
            .Where(r => r.NoteId == nid)
            .OrderByDescending(r => r.Confidence)
            .Take(maxResults)
            .Join(db.Notes,
                r => r.RelatedNoteId,
                n => n.Id,
                (r, n) => new RelatedNoteView(
                    n.Id.ToString(),
                    n.Content,
                    r.RelationType,
                    r.Confidence,
                    r.Similarity,
                    r.Description))
            .ToListAsync(ct).ConfigureAwait(false);

        return new FindRelatedNotesResponse(rows);
    }

    [McpServerTool(Name = "get_entity")]
    [Description("Fetch a single entity by canonical name and its 1-hop neighbours in the knowledge graph " +
                 "(both outgoing and incoming edges). Returns null if not found.")]
    public static async Task<GetEntityResponse> GetEntityAsync(
        IGraphContext graph,
        ITenantContext tenant,
        [Description("Entity name (canonical, usually lowercase, hyphen-separated for multi-word).")] string name,
        [Description("Maximum number of edges to return per direction (default 25).")] int maxEdges = 25,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();

        var matches = await graph.GetEntitiesAsync(scope.Project, nameFilter: name, limit: 1, ct);
        if (matches.Count == 0)
        {
            return new GetEntityResponse(null, Array.Empty<RelatedEdgeView>(), Array.Empty<RelatedEdgeView>());
        }

        var entity = matches[0];
        var outgoing = await graph.GetEdgesAsync(scope.Project, from: entity.Id, ct: ct);
        var incoming = await graph.GetEdgesAsync(scope.Project, to: entity.Id, ct: ct);

        var entityView = new EntityView(
            entity.Id.ToString(),
            entity.Name,
            entity.Kind,
            new Dictionary<string, string>(entity.Attributes),
            entity.FirstSeenAt.ToString("o"),
            entity.LastSeenAt.ToString("o"));

        return new GetEntityResponse(
            entityView,
            outgoing.Take(maxEdges).Select(e => RelatedEdgeView.From(e, otherEntity: e.To)).ToArray(),
            incoming.Take(maxEdges).Select(e => RelatedEdgeView.From(e, otherEntity: e.From)).ToArray());
    }
}

public sealed record SaveEpisodeResponse(
    string EpisodeId,
    IReadOnlyList<string> NoteIds,
    IReadOnlyList<string> EntityIds);

public sealed record ReflectMemoryResponse(
    string ReflectionId,
    string Scope,
    string Summary,
    int NotesConsidered);

public sealed record RelatedNoteView(
    string NoteId,
    string Content,
    string RelationType,
    double Confidence,
    double Similarity,
    string? Description);

public sealed record FindRelatedNotesResponse(
    IReadOnlyList<RelatedNoteView> Relations);

public sealed record SearchMemoryHit(
    string NoteId,
    string Content,
    double Score,
    IReadOnlyList<string> RelatedEntityIds);

public sealed record SearchMemoryResponse(
    IReadOnlyList<SearchMemoryHit> Hits,
    int TotalCandidates);

public sealed record EntityView(
    string Id,
    string Name,
    string Kind,
    Dictionary<string, string> Attributes,
    string FirstSeenAt,
    string LastSeenAt);

public sealed record RelatedEdgeView(
    string EdgeId,
    string OtherEntityId,
    string Relation,
    string RecordedAt,
    string? ValidFrom,
    string? ValidTo,
    string? InvalidatedAt)
{
    internal static RelatedEdgeView From(Memory.Domain.Edge e, Memory.Domain.EntityId otherEntity) => new(
        e.Id.ToString(),
        otherEntity.ToString(),
        e.Relation,
        e.RecordedAt.ToString("o"),
        e.ValidFrom?.ToString("o"),
        e.ValidTo?.ToString("o"),
        e.InvalidatedAt?.ToString("o"));
}

public sealed record GetEntityResponse(
    EntityView? Entity,
    IReadOnlyList<RelatedEdgeView> OutgoingEdges,
    IReadOnlyList<RelatedEdgeView> IncomingEdges);
