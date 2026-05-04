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
    ILlmGateway llm,
    IReranker reranker,
    IGraphRetriever graphRetriever) : ISearchPipeline
{
    private const int CandidateMultiplier = 4;     // pull 4× max from each retriever before fusion
    private const int RrfK = 60;                    // standard RRF constant

    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        _ = tenant.Require();

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new SearchResult(Array.Empty<SearchHit>(), 0);
        }

        var candidateLimit = Math.Max(20, request.MaxResults * CandidateMultiplier);

        var queryEmbedding = await llm.GetEmbeddings()
            .GenerateVectorAsync(request.Query, cancellationToken: ct)
            .ConfigureAwait(false);
        var queryVector = new Pgvector.Vector(queryEmbedding.ToArray());

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        // Sequential retrieval — all three retrievers share the same DbContext / NpgsqlConnection
        // so they cannot run in parallel ("a command is already in progress").
        var vectorHits = await VectorSearchAsync(conn, queryVector, request, candidateLimit, ct).ConfigureAwait(false);
        var bm25Hits = await Bm25SearchAsync(conn, request, candidateLimit, ct).ConfigureAwait(false);
        var graphRaw = await graphRetriever.RetrieveAsync(request.Query, candidateLimit, ct).ConfigureAwait(false);

        var graphHits = graphRaw
            .Select((h, i) => new RankedHit(h.NoteId, h.Content, i + 1, 1.0 - h.Score, h.RelatedEntities, FromVector: false, FromBm25: false))
            .ToList();

        var fused = FuseRrf(vectorHits, bm25Hits, graphHits, RrfK);

        var reranked = await reranker.RerankAsync(request.Query, fused, ct).ConfigureAwait(false);
        var top = reranked.Take(request.MaxResults).ToList();

        return new SearchResult(top, fused.Count);
    }

    private static async Task<List<RankedHit>> VectorSearchAsync(
        NpgsqlConnection conn,
        Pgvector.Vector queryVector,
        SearchRequest request,
        int limit,
        CancellationToken ct)
    {
        var sql = new StringBuilder("""
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
        cmd.Parameters.AddWithValue("limit", limit);
        AppendFilters(sql, cmd, request);
        sql.Append(" ORDER BY distance ASC LIMIT @limit");
        cmd.CommandText = sql.ToString();

        var hits = new List<RankedHit>();
        var rank = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rank++;
            var noteId = new NoteId(reader.GetGuid(0));
            var content = reader.GetString(1);
            var distance = reader.GetDouble(2);
            var related = ((Guid[])reader.GetValue(3)).Select(g => new EntityId(g)).ToArray();
            hits.Add(new RankedHit(noteId, content, rank, distance, related, FromVector: true, FromBm25: false));
        }
        return hits;
    }

    private static async Task<List<RankedHit>> Bm25SearchAsync(
        NpgsqlConnection conn,
        SearchRequest request,
        int limit,
        CancellationToken ct)
    {
        var sql = new StringBuilder("""
            SELECT n.id,
                   n.content,
                   ts_rank(n.content_tsv, plainto_tsquery('simple', @qtext)) AS bm_rank,
                   COALESCE(
                     (SELECT array_agg(m.entity_id)
                      FROM memory.note_entity_mentions m
                      WHERE m.note_id = n.id),
                     ARRAY[]::uuid[]) AS related_entity_ids
            FROM memory.notes n
            WHERE n.superseded_at IS NULL
              AND n.content_tsv @@ plainto_tsquery('simple', @qtext)
            """);

        await using var cmd = new NpgsqlCommand { Connection = conn };
        cmd.Parameters.AddWithValue("qtext", request.Query);
        cmd.Parameters.AddWithValue("limit", limit);
        AppendFilters(sql, cmd, request);
        sql.Append(" ORDER BY bm_rank DESC LIMIT @limit");
        cmd.CommandText = sql.ToString();

        var hits = new List<RankedHit>();
        var rank = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rank++;
            var noteId = new NoteId(reader.GetGuid(0));
            var content = reader.GetString(1);
            var bmRank = reader.GetFloat(2);
            var related = ((Guid[])reader.GetValue(3)).Select(g => new EntityId(g)).ToArray();
            hits.Add(new RankedHit(noteId, content, rank, 1.0 - bmRank, related, FromVector: false, FromBm25: true));
        }
        return hits;
    }

    private static void AppendFilters(StringBuilder sql, NpgsqlCommand cmd, SearchRequest request)
    {
        if (request.Tags is { Count: > 0 })
        {
            sql.Append(" AND n.tags && @tags");
            cmd.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = request.Tags.ToArray(),
            });
        }
        if (request.Since.HasValue)
        {
            sql.Append(" AND n.created_at >= @since");
            cmd.Parameters.AddWithValue("since", request.Since.Value);
        }
        if (request.Until.HasValue)
        {
            sql.Append(" AND n.created_at <= @until");
            cmd.Parameters.AddWithValue("until", request.Until.Value);
        }
    }

    /// <summary>
    /// Reciprocal Rank Fusion across three retrievers (vector, BM25, graph PPR).
    /// A note's RRF score is the sum of 1/(k + rank_in_each_list_it_appears_in).
    /// Hits found by multiple retrievers stack contributions — that's the whole point.
    /// </summary>
    private static List<SearchHit> FuseRrf(List<RankedHit> vector, List<RankedHit> bm25, List<RankedHit> graph, int k)
    {
        var pool = new Dictionary<NoteId, FusedHit>();

        Add(pool, vector, k, fromVector: true);
        Add(pool, bm25, k, fromBm25: true);
        Add(pool, graph, k, fromGraph: true);

        return pool.Values
            .OrderByDescending(f => f.RrfScore)
            .Select(f => new SearchHit(f.NoteId, f.Content, f.RrfScore, f.Related))
            .ToList();
    }

    private static void Add(
        Dictionary<NoteId, FusedHit> pool,
        List<RankedHit> hits,
        int k,
        bool fromVector = false,
        bool fromBm25 = false,
        bool fromGraph = false)
    {
        foreach (var h in hits)
        {
            var contribution = 1.0 / (k + h.Rank);
            if (pool.TryGetValue(h.NoteId, out var existing))
            {
                existing.RrfScore += contribution;
                if (fromVector) existing.FromVector = true;
                if (fromBm25) existing.FromBm25 = true;
                if (fromGraph) existing.FromGraph = true;
                if (existing.Related.Length == 0 && h.Related.Length > 0) existing.Related = h.Related;
            }
            else
            {
                pool[h.NoteId] = new FusedHit
                {
                    NoteId = h.NoteId,
                    Content = h.Content,
                    Related = h.Related,
                    RrfScore = contribution,
                    FromVector = fromVector,
                    FromBm25 = fromBm25,
                    FromGraph = fromGraph,
                };
            }
        }
    }

    private sealed record RankedHit(
        NoteId NoteId,
        string Content,
        int Rank,
        double Distance,
        EntityId[] Related,
        bool FromVector,
        bool FromBm25);

    private sealed class FusedHit
    {
        public required NoteId NoteId { get; init; }
        public required string Content { get; init; }
        public EntityId[] Related { get; set; } = Array.Empty<EntityId>();
        public double RrfScore { get; set; }
        public bool FromVector { get; set; }
        public bool FromBm25 { get; set; }
        public bool FromGraph { get; set; }
    }
}
