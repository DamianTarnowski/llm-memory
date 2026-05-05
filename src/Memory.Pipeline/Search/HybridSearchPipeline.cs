using System.Data;
using System.Text;
using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Memory.Pipeline.Search;

internal sealed class HybridSearchPipeline(
    ITenantContext tenant,
    MemoryDbContext db,
    ILlmGateway llm,
    IReranker reranker,
    IGraphRetriever graphRetriever,
    IQueryExpander queryExpander,
    IOptions<TimeDecayOptions> timeDecayOptions,
    TimeProvider time) : ISearchPipeline
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

        // Optional query expansion — for short queries, generate variants and embed each.
        // The original query stays as variants[0] so downstream BM25/graph still use it untouched.
        var variants = await queryExpander.ExpandAsync(request.Query, ct).ConfigureAwait(false);

        // Single batched embedding call across all variants.
        var embeddings = await llm.GetEmbeddings()
            .GenerateAsync(variants.ToList(), cancellationToken: ct)
            .ConfigureAwait(false);

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        // Sequential retrieval — all three retrievers share the same DbContext / NpgsqlConnection
        // so they cannot run in parallel ("a command is already in progress"). Per-variant
        // vector hits are RRF-fused into a single vector stream before joining BM25 and graph.
        var vectorPerVariant = new List<List<RankedHit>>(variants.Count);
        for (var i = 0; i < variants.Count; i++)
        {
            var queryVector = new Pgvector.Vector(embeddings[i].Vector.ToArray());
            var hits = await VectorSearchAsync(conn, queryVector, request, candidateLimit, ct).ConfigureAwait(false);
            vectorPerVariant.Add(hits);
        }
        var vectorHits = MergeVectorStreams(vectorPerVariant, RrfK);

        var bm25Hits = await Bm25SearchAsync(conn, request, candidateLimit, ct).ConfigureAwait(false);
        var graphRaw = await graphRetriever.RetrieveAsync(request.Query, candidateLimit, ct).ConfigureAwait(false);

        var graphHits = graphRaw
            .Select((h, i) => new RankedHit(h.NoteId, h.Content, i + 1, 1.0 - h.Score, h.RelatedEntities, FromVector: false, FromBm25: false))
            .ToList();

        var fused = FuseRrf(vectorHits, bm25Hits, graphHits, RrfK);

        // Optional time-decay: re-weight by note age before reranking.
        var decayed = await ApplyTimeDecayAsync(fused, ct).ConfigureAwait(false);

        var reranked = await reranker.RerankAsync(request.Query, decayed, ct).ConfigureAwait(false);
        var top = reranked.Take(request.MaxResults).ToList();

        // Optional token-budget pack — keep top-by-score until adding the next hit would
        // exceed MaxTokens. Conservative estimator (chars / 3.8) so we round up tokens
        // and leave a margin under the model's actual window.
        if (request.MaxTokens is { } budget && budget > 0 && top.Count > 0)
        {
            var packed = new List<SearchHit>(top.Count);
            var used = 0;
            foreach (var h in top)
            {
                var cost = EstimateTokens(h.Content) + 32;  // 32 = JSON overhead per hit
                if (packed.Count > 0 && used + cost > budget) break;
                packed.Add(h);
                used += cost;
            }
            top = packed;
        }

        return new SearchResult(top, fused.Count);
    }

    private static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 3.8));

    private async Task<IReadOnlyList<SearchHit>> ApplyTimeDecayAsync(IReadOnlyList<SearchHit> hits, CancellationToken ct)
    {
        var opts = timeDecayOptions.Value;
        if (!opts.Enabled || hits.Count == 0) return hits;

        var noteIds = hits.Select(h => h.NoteId.Value).ToArray();
        var ages = await db.Notes
            .FromSqlInterpolated($"SELECT * FROM memory.notes WHERE id = ANY({noteIds})")
            .Select(n => new { n.Id, n.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var ageLookup = ages.ToDictionary(n => n.Id, n => n.CreatedAt);

        var now = time.GetUtcNow();
        var lambda = Math.Log(2.0) / Math.Max(0.001, opts.HalfLifeDays);

        return hits
            .Select(h =>
            {
                if (!ageLookup.TryGetValue(h.NoteId, out var createdAt)) return h;
                var ageDays = Math.Max(0.0, (now - createdAt).TotalDays);
                var multiplier = Math.Max(opts.MinMultiplier, Math.Exp(-lambda * ageDays));
                return h with { Score = h.Score * multiplier };
            })
            .OrderByDescending(h => h.Score)
            .ToList();
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
        if (request.Kinds is { Count: > 0 })
        {
            sql.Append(" AND n.kind = ANY(@kinds)");
            cmd.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
            {
                Value = request.Kinds.Select(k => (short)k).ToArray(),
            });
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

        Add(pool, vector, k, stream: Stream.Vector);
        Add(pool, bm25, k, stream: Stream.Bm25);
        Add(pool, graph, k, stream: Stream.Graph);

        return pool.Values
            .OrderByDescending(f => f.RrfScore)
            .Select(f => new SearchHit(
                f.NoteId, f.Content, f.RrfScore, f.Related,
                new SearchHitProvenance(
                    FromVector: f.FromVector,
                    FromBm25: f.FromBm25,
                    FromGraph: f.FromGraph,
                    VectorScore: f.VectorScore,
                    Bm25Score: f.Bm25Score,
                    GraphScore: f.GraphScore,
                    RerankerScore: null)))
            .ToList();
    }

    /// <summary>
    /// Fuses per-variant vector hit lists into a single re-ranked stream. Each variant's
    /// hits get RRF contributions; the resulting stream is then sorted by combined score
    /// and re-ranked 1..N before joining the main 3-stream fusion.
    /// </summary>
    private static List<RankedHit> MergeVectorStreams(List<List<RankedHit>> perVariant, int k)
    {
        if (perVariant.Count == 1) return perVariant[0];

        var pool = new Dictionary<NoteId, (RankedHit Sample, double Score)>();
        foreach (var list in perVariant)
        {
            foreach (var h in list)
            {
                var contribution = 1.0 / (k + h.Rank);
                if (pool.TryGetValue(h.NoteId, out var existing))
                {
                    pool[h.NoteId] = (existing.Sample, existing.Score + contribution);
                }
                else
                {
                    pool[h.NoteId] = (h, contribution);
                }
            }
        }

        return pool.Values
            .OrderByDescending(p => p.Score)
            .Select((p, i) => p.Sample with { Rank = i + 1 })
            .ToList();
    }

    private enum Stream { Vector, Bm25, Graph }

    private static void Add(Dictionary<NoteId, FusedHit> pool, List<RankedHit> hits, int k, Stream stream)
    {
        foreach (var h in hits)
        {
            var contribution = 1.0 / (k + h.Rank);
            if (!pool.TryGetValue(h.NoteId, out var existing))
            {
                existing = new FusedHit
                {
                    NoteId = h.NoteId,
                    Content = h.Content,
                    Related = h.Related,
                };
                pool[h.NoteId] = existing;
            }

            existing.RrfScore += contribution;
            if (existing.Related.Length == 0 && h.Related.Length > 0) existing.Related = h.Related;

            switch (stream)
            {
                case Stream.Vector: existing.FromVector = true; existing.VectorScore = contribution; break;
                case Stream.Bm25: existing.FromBm25 = true; existing.Bm25Score = contribution; break;
                case Stream.Graph: existing.FromGraph = true; existing.GraphScore = contribution; break;
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
        public double VectorScore { get; set; }
        public double Bm25Score { get; set; }
        public double GraphScore { get; set; }
    }
}
