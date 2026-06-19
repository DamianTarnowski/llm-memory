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
    IImportanceJudge importanceJudge,
    IOptions<LlmOptions> llmOptions,
    IOptions<SaveFilterOptions> saveFilterOptions,
    IEmbeddingBackfillQueue embeddingBackfillQueue,
    TimeProvider time,
    ILogger<SimpleIngestionPipeline> logger) : IIngestionPipeline
{
    public async Task<IngestionResult> IngestAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var now = time.GetUtcNow();

        // Optional save filter — LLM judge decides whether the candidate is worth keeping
        // before we spend embedding + extraction cost. Disabled by default (fail-open).
        var filterOpts = saveFilterOptions.Value;
        if (filterOpts.Enabled && !request.ForceSave)
        {
            var judgment = await importanceJudge.JudgeAsync(request.Source, request.Content, ct).ConfigureAwait(false);
            logger.LogInformation("Save filter judgment: score={Score:F2} save={Save} reason={Reason}",
                judgment.Score, judgment.Save, judgment.Reason);
            if (!judgment.Save || judgment.Score < filterOpts.MinScore)
            {
                logger.LogInformation("Save filter DROPPED episode (score {Score:F2} < {MinScore:F2}).",
                    judgment.Score, filterOpts.MinScore);
                return new IngestionResult(
                    EpisodeId: null,
                    Notes: Array.Empty<NoteId>(),
                    EntitiesUpserted: Array.Empty<EntityId>(),
                    Skipped: true,
                    SkipReason: judgment.Reason,
                    ImportanceScore: judgment.Score);
            }
        }

        var memoryTypeOverride = request.MemoryTypeOverride ?? TryParseMemoryType(request.Metadata);
        var noteKindOverride = request.NoteKindOverride ?? TryParseNoteKind(request.Metadata);
        var extraction = request.DirectNote
            ? BuildDirectExtraction(
                request,
                memoryTypeOverride ?? MemoryType.Semantic,
                noteKindOverride ?? NoteKind.General)
            : await extractor.ExtractAsync(request.Content, ct).ConfigureAwait(false);

        var embeddingVectors = new List<float[]>(extraction.Notes.Count);
        if (!request.DeferEmbedding)
        {
            // Single batched embedding call for all atomic notes.
            var embeddings = await llm.GetEmbeddings()
                .GenerateAsync(extraction.Notes.Select(n => n.Content).ToList(), cancellationToken: ct)
                .ConfigureAwait(false);
            embeddingVectors.AddRange(embeddings.Select(e => e.Vector.ToArray()));
        }

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
                Kind = noteKindOverride ?? ext.Kind,
                MemoryType = memoryTypeOverride ?? ext.MemoryType,
                CreatedAt = now,
            };
            notes.Add(note);

            if (!request.DeferEmbedding)
            {
                var vec = embeddingVectors[i];
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

        if (request.DeferEmbedding)
        {
            await embeddingBackfillQueue.EnqueueAsync(
                scope,
                notes.Select(n => n.Id).ToArray(),
                ct).ConfigureAwait(false);
        }
        else
        {
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

    private static MemoryType? TryParseMemoryType(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        return TryGet(metadata, "memory_type", out var raw) || TryGet(metadata, "memoryType", out raw)
            ? ParseEnum<MemoryType>(raw)
            : null;
    }

    private static NoteKind? TryParseNoteKind(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        return TryGet(metadata, "note_kind", out var raw) || TryGet(metadata, "kind", out raw)
            ? ParseEnum<NoteKind>(raw)
            : null;
    }

    private static ExtractionResult BuildDirectExtraction(
        IngestionRequest request,
        MemoryType memoryType,
        NoteKind kind)
    {
        var context = ReadMetadata(request.Metadata, "applies_to")
                      ?? ReadMetadata(request.Metadata, "scope")
                      ?? request.Source;
        var tags = ParseMetadataList(request.Metadata, "tags")
            .Concat(new[] { request.Source, memoryType.ToString(), kind.ToString() })
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return new ExtractionResult(
            new List<ExtractedNote>
            {
                new(
                    Content: request.Content.Trim(),
                    ContextDescription: context,
                    Keywords: ParseMetadataList(request.Metadata, "keywords"),
                    Tags: tags,
                    Kind: kind,
                    MemoryType: memoryType),
            },
            new List<ExtractedEntity>(),
            new List<ExtractedRelationship>());
    }

    private static string? ReadMetadata(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata is not null && TryGet(metadata, key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static List<string> ParseMetadataList(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        var raw = ReadMetadata(metadata, key);
        if (raw is null) return new List<string>();
        return raw
            .Split(new[] { ',', '|', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> values, string key, out string value)
    {
        foreach (var kv in values)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = "";
        return false;
    }

    private static TEnum? ParseEnum<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().Replace("-", "_", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name, ignoreCase: true);
            }
        }
        return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }
}
