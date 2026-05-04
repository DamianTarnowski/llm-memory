using Memory.Domain;
using Memory.Llm;
using Memory.Pipeline.Linking;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Ingestion;

internal sealed class SimpleIngestionPipeline(
    ITenantContext tenant,
    MemoryDbContext db,
    IGraphContext graph,
    ILlmGateway llm,
    IExtractor extractor,
    INoteLinker linker,
    IOptions<LlmOptions> llmOptions,
    TimeProvider time,
    ILogger<SimpleIngestionPipeline> logger) : IIngestionPipeline
{
    public async Task<IngestionResult> IngestAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var now = time.GetUtcNow();

        var extraction = await extractor.ExtractAsync(request.Content, ct).ConfigureAwait(false);

        var embeddingVector = await llm.GetEmbeddings()
            .GenerateVectorAsync(extraction.Note.Content, cancellationToken: ct)
            .ConfigureAwait(false);

        var episode = new Episode
        {
            Id = EpisodeId.New(),
            Project = scope.Project,
            Source = request.Source,
            Content = request.Content,
            OccurredAt = request.OccurredAt,
            IngestedAt = now,
            Metadata = request.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>(),
        };

        var note = new Note
        {
            Id = NoteId.New(),
            Project = scope.Project,
            SourceEpisode = episode.Id,
            Content = extraction.Note.Content,
            ContextDescription = extraction.Note.ContextDescription,
            Keywords = extraction.Note.Keywords,
            Tags = extraction.Note.Tags,
            CreatedAt = now,
        };

        var noteEmbedding = new NoteEmbedding
        {
            NoteId = note.Id,
            Project = scope.Project,
            EmbeddingModel = llmOptions.Value.EmbeddingModel,
            Dimensions = embeddingVector.Length,
            Embedding = embeddingVector.ToArray(),
            CreatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        db.Episodes.Add(episode);
        db.Notes.Add(note);
        db.NoteEmbeddings.Add(noteEmbedding);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var entityMap = new Dictionary<string, EntityId>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in extraction.Entities)
        {
            if (string.IsNullOrWhiteSpace(ext.Name)) continue;

            var id = await graph.UpsertEntityAsync(
                scope.Project,
                ext.Name,
                string.IsNullOrWhiteSpace(ext.Kind) ? "concept" : ext.Kind,
                ext.Attributes,
                now,
                ct).ConfigureAwait(false);
            entityMap[ext.Name] = id;

            db.NoteEntityMentions.Add(new NoteEntityMention
            {
                NoteId = note.Id,
                EntityId = id,
                Project = scope.Project,
                CreatedAt = now,
            });
        }
        if (entityMap.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Bi-temporal: invalidate any prior edges the LLM marked as superseded by this episode.
        // Resolve entity ids from local map first, fall back to graph lookup for older entities.
        if (extraction.SupersedesPriorEdges is { Count: > 0 } supersedes)
        {
            foreach (var s in supersedes)
            {
                if (string.IsNullOrWhiteSpace(s.From) || string.IsNullOrWhiteSpace(s.To) || string.IsNullOrWhiteSpace(s.Relation)) continue;

                var fromId = await ResolveEntityIdAsync(s.From, entityMap, scope.Project, ct).ConfigureAwait(false);
                var toId = await ResolveEntityIdAsync(s.To, entityMap, scope.Project, ct).ConfigureAwait(false);
                if (fromId is null || toId is null) continue;

                var existing = await graph.GetEdgesAsync(scope.Project, from: fromId, to: toId, relation: s.Relation, ct: ct).ConfigureAwait(false);
                foreach (var e in existing.Where(e => e.InvalidatedAt is null))
                {
                    await graph.InvalidateEdgeAsync(e.Id, now, ct).ConfigureAwait(false);
                }
            }
        }

        foreach (var ext in extraction.Relationships)
        {
            if (!entityMap.TryGetValue(ext.From, out var fromId)) continue;
            if (!entityMap.TryGetValue(ext.To, out var toId)) continue;
            if (string.IsNullOrWhiteSpace(ext.Relation)) continue;

            var edge = new Edge
            {
                Id = EdgeId.New(),
                Project = scope.Project,
                From = fromId,
                To = toId,
                Relation = ext.Relation,
                RecordedAt = now,
                ValidFrom = now,
                SourceEpisode = episode.Id,
                Properties = ext.Properties,
            };
            await graph.AddEdgeAsync(edge, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        try
        {
            var linked = await linker.LinkRecentNoteAsync(note.Id, embeddingVector.ToArray(), ct).ConfigureAwait(false);
            if (linked > 0)
            {
                logger.LogInformation("Linked note {NoteId} to {Count} related notes via A-MEM auto-linker.", note.Id, linked);
            }
        }
        catch (Exception ex)
        {
            // Linking is best-effort: never let it fail the ingest.
            logger.LogWarning(ex, "A-MEM auto-linking failed for note {NoteId}; continuing.", note.Id);
        }

        return new IngestionResult(
            episode.Id,
            [note.Id],
            entityMap.Values.ToList());
    }

    private async Task<EntityId?> ResolveEntityIdAsync(
        string name,
        Dictionary<string, EntityId> entityMap,
        ProjectId project,
        CancellationToken ct)
    {
        if (entityMap.TryGetValue(name, out var id)) return id;
        var existing = await graph.GetEntitiesAsync(project, nameFilter: name, limit: 1, ct).ConfigureAwait(false);
        return existing.Count > 0 ? existing[0].Id : null;
    }
}
