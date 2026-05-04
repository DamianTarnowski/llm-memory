using System.Data;
using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.Pipeline.Linking;

internal sealed class LlmNoteLinker(
    ITenantContext tenant,
    MemoryDbContext db,
    ILlmGateway llm,
    IOptions<LinkingOptions> options,
    TimeProvider time,
    ILogger<LlmNoteLinker> logger) : INoteLinker
{
    private const string SystemPrompt = """
        You judge whether two short memory notes describe semantically related concepts. Be strict —
        only mark as related when there is a clear conceptual overlap: same topic, complementary
        information, direct contradiction, supersedence, or a clear hierarchical relation.

        Choose ONE relation type from this list:
          - related-to: same topic, no other strong relation
          - extends: B adds detail or follow-up to A
          - specializes: A is a general principle, B is a specific case
          - contradicts: A and B make incompatible claims
          - supports: B provides evidence or argument for A
          - supersedes: B replaces or invalidates A (newer truth)
          - parallels: A and B describe analogous but separate things
          - duplicates: A and B are essentially the same fact

        Return Confidence in 0..1. If you have any doubt, return IsRelated=false.
        Description is one sentence (under 200 chars) explaining the relation, or empty if not related.
        """;

    public async Task<int> LinkRecentNoteAsync(NoteId noteId, float[] noteEmbedding, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled || noteEmbedding.Length == 0) return 0;

        var scope = tenant.Require();
        var now = time.GetUtcNow();

        var candidates = await VectorSearchAsync(noteId, noteEmbedding, opts.NeighborCount, opts.MinSimilarity, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        var sourceNote = await db.Notes
            .Where(n => n.Id == noteId)
            .Select(n => new { n.Content, n.ContextDescription })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (sourceNote is null) return 0;

        var created = 0;
        var supersedeCandidate = false;
        foreach (var (candidateId, candidateContent, similarity) in candidates)
        {
            try
            {
                var judgment = await JudgeAsync(sourceNote.Content, candidateContent, ct).ConfigureAwait(false);
                if (!judgment.IsRelated || judgment.Confidence < opts.MinConfidence) continue;

                var existing = await db.NoteRelations
                    .Where(r => r.NoteId == noteId && r.RelatedNoteId == candidateId)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (existing is not null) continue;

                var relationType = string.IsNullOrWhiteSpace(judgment.RelationType) ? "related-to" : judgment.RelationType;
                db.NoteRelations.Add(new NoteRelation
                {
                    NoteId = noteId,
                    RelatedNoteId = candidateId,
                    Project = scope.Project,
                    RelationType = relationType,
                    Confidence = judgment.Confidence,
                    Similarity = similarity,
                    Description = string.IsNullOrWhiteSpace(judgment.Description) ? null : judgment.Description,
                    CreatedAt = now,
                });
                created++;

                if (relationType.Equals("duplicates", StringComparison.OrdinalIgnoreCase)
                    && judgment.Confidence >= opts.DuplicateSupersedeConfidence)
                {
                    supersedeCandidate = true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Note linker failed to judge {SourceNoteId} <-> {CandidateId}", noteId, candidateId);
            }
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (supersedeCandidate)
        {
            await db.Notes
                .Where(n => n.Id == noteId)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.SupersededAt, _ => now), ct)
                .ConfigureAwait(false);
            logger.LogInformation("Note {NoteId} superseded immediately as duplicate of an existing note.", noteId);
        }

        return created;
    }

    private async Task<List<(NoteId Id, string Content, double Similarity)>> VectorSearchAsync(
        NoteId excludeId,
        float[] embedding,
        int limit,
        double minSimilarity,
        CancellationToken ct)
    {
        var queryVector = new Pgvector.Vector(embedding);

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        const string sql = """
            SELECT n.id, n.content, ne.embedding <=> @query AS distance
            FROM memory.notes n
            JOIN memory.note_embeddings ne ON ne.note_id = n.id
            WHERE n.superseded_at IS NULL AND n.id <> @exclude_id
            ORDER BY distance ASC
            LIMIT @limit
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("query", queryVector);
        cmd.Parameters.AddWithValue("exclude_id", excludeId.Value);
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<(NoteId, string, double)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = new NoteId(reader.GetGuid(0));
            var content = reader.GetString(1);
            var distance = reader.GetDouble(2);
            var similarity = Math.Max(0.0, 1.0 - distance);
            if (similarity < minSimilarity) continue;
            results.Add((id, content, similarity));
        }
        return results;
    }

    private async Task<LinkJudgment> JudgeAsync(string sourceContent, string candidateContent, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Note A:\n{sourceContent}\n\nNote B:\n{candidateContent}"),
        };

        var response = await llm.GetChat()
            .GetResponseAsync<LinkJudgment>(messages, cancellationToken: ct)
            .ConfigureAwait(false);

        return response.Result ?? new LinkJudgment(false, "related-to", 0.0, "");
    }
}
