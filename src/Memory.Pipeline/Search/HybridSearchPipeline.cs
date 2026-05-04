using System.Data;
using System.Text;
using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;
using NpgsqlTypes;

namespace Memory.Pipeline.Search;

internal sealed class HybridSearchPipeline(
    ITenantContext tenant,
    MemoryDbContext db,
    ILlmGateway llm) : ISearchPipeline
{
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        _ = tenant.Require();

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new SearchResult(Array.Empty<SearchHit>(), 0);
        }

        var queryEmbedding = await llm.GetEmbeddings()
            .GenerateVectorAsync(request.Query, cancellationToken: ct)
            .ConfigureAwait(false);
        var queryVector = new Pgvector.Vector(queryEmbedding.ToArray());

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        var sqlBuilder = new StringBuilder("""
            SELECT n.id,
                   n.content,
                   ne.embedding <=> @query AS distance,
                   COALESCE(
                     (SELECT array_agg(m.entity_id)
                      FROM memory.note_entity_mentions m
                      WHERE m.note_id = n.id),
                     ARRAY[]::uuid[]) AS related_entity_ids
            FROM memory.notes n
            JOIN memory.note_embeddings ne ON ne.note_id = n.id
            WHERE n.superseded_at IS NULL
            """);

        await using var cmd = new NpgsqlCommand { Connection = conn };
        cmd.Parameters.AddWithValue("query", queryVector);
        cmd.Parameters.AddWithValue("limit", request.MaxResults);

        if (request.Tags is { Count: > 0 })
        {
            sqlBuilder.Append(" AND n.tags && @tags");
            cmd.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = request.Tags.ToArray(),
            });
        }

        if (request.Since.HasValue)
        {
            sqlBuilder.Append(" AND n.created_at >= @since");
            cmd.Parameters.AddWithValue("since", request.Since.Value);
        }

        if (request.Until.HasValue)
        {
            sqlBuilder.Append(" AND n.created_at <= @until");
            cmd.Parameters.AddWithValue("until", request.Until.Value);
        }

        sqlBuilder.Append(" ORDER BY distance ASC LIMIT @limit");
        cmd.CommandText = sqlBuilder.ToString();

        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var noteId = new NoteId(reader.GetGuid(0));
            var content = reader.GetString(1);
            var distance = reader.GetDouble(2);
            var score = Math.Max(0.0, 1.0 - distance);

            var relatedRaw = (Guid[])reader.GetValue(3);
            var related = relatedRaw.Select(g => new EntityId(g)).ToArray();

            hits.Add(new SearchHit(noteId, content, score, related));
        }

        return new SearchResult(hits, hits.Count);
    }
}
