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

        // Single batched embedding call for all atomic notes.
        var embeddings = await llm.GetEmbeddings()
            .GenerateAsync(extraction.Notes.Select(n => n.Content).ToList(), cancellationToken: ct)
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

        var notes = new List<Note>(extraction.Notes.Count);
        var noteEmbeddings = new List<NoteEmbedding>(extraction.Notes.Count);
        for (var i = 0; i < extraction.Notes.Count; i++)
        {
            var ext = extraction.Notes[i];
            var note = new Note
            {
                Id = NoteId.New(),
                Project = scope.Project,
                SourceEpisode = episode.Id,
                Content = ext.Content,
                ContextDescription = ext.ContextDescription,
                Keywords = ext.Keywords,
                Tags = ext.Tags,
                CreatedAt = now,
            };
            notes.Add(note);

            var vec = embeddings[i].Vector.ToArray();
            noteEmbeddings.Add(new NoteEmbedding
            {
                NoteId = note.Id,
                Project = scope.Project,
                EmbeddingModel = llmOptions.Value.EmbeddingModel,
                Dimensions = vec.Length,
                Embedding = vec,
                CreatedAt = now,
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        db.Episodes.Add(episode);
        db.Notes.AddRange(notes);
        db.NoteEmbeddings.AddRange(noteEmbeddings);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Upsert entities (mentioned across all notes — entity mentions per-note for retrieval).
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

            // Mention every entity from every note in the episode (proxy for "this episode's entities").
            // A finer-grained per-note mention would require LLM to assign entities-per-note.
            foreach (var n in notes)
            {
                db.NoteEntityMentions.Add(new NoteEntityMention
                {
                    NoteId = n.Id,
                    EntityId = id,
                    Project = scope.Project,
                    CreatedAt = now,
                });
            }
        }
        if (entityMap.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Bi-temporal: invalidate prior edges marked as superseded.
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

        // A-MEM auto-linking — best-effort, per note, after main commit.
        try
        {
            for (var i = 0; i < notes.Count; i++)
            {
                var linked = await linker
                    .LinkRecentNoteAsync(notes[i].Id, noteEmbeddings[i].Embedding, ct)
                    .ConfigureAwait(false);
                if (linked > 0)
                {
                    logger.LogInformation("A-MEM linked note {NoteId} to {Count} prior notes.", notes[i].Id, linked);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A-MEM auto-linking failed for episode {EpisodeId}; continuing.", episode.Id);
        }

        return new IngestionResult(
            episode.Id,
            notes.Select(n => n.Id).ToList(),
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
