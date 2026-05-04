using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Ingestion;

internal sealed class SimpleIngestionPipeline(
    ITenantContext tenant,
    MemoryDbContext db,
    IGraphContext graph,
    ILlmGateway llm,
    IExtractor extractor,
    IOptions<LlmOptions> llmOptions,
    TimeProvider time) : IIngestionPipeline
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

        return new IngestionResult(
            episode.Id,
            [note.Id],
            entityMap.Values.ToList());
    }
}
