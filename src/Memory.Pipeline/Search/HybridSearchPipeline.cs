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
    IQueryRouter queryRouter,
    ImageEmbedderHolder imageEmbedderHolder,
    IOptions<TimeDecayOptions> timeDecayOptions,
    IOptions<AbstentionOptions> abstentionOptions,
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

        var route = await queryRouter.RouteAsync(request, ct).ConfigureAwait(false);
        if (!route.ShouldSearch)
        {
            return new SearchResult(
                Array.Empty<SearchHit>(),
                0,
                Abstain: true,
                AbstainReason: route.SkipReason ?? "Router skipped retrieval.",
                Route: route.ToTrace(request.Query, Array.Empty<string>()));
        }

        var effectiveMaxResults = Math.Min(request.MaxResults, route.MaxResults);
        var effectiveRequest = request with
        {
            Query = route.StandaloneQuery,
            MaxResults = effectiveMaxResults,
        };
        var candidateLimit = Math.Max(20, effectiveRequest.MaxResults * CandidateMultiplier);

        // Optional query routing + expansion. The effective standalone query stays
        // variants[0]. Router/expander variants feed vector recall; BM25/graph use
        // the canonical standalone query for precision and explainability.
        var variants = await BuildQueryVariantsAsync(effectiveRequest.Query, route, ct).ConfigureAwait(false);
        var routeTrace = route.ToTrace(request.Query, variants);

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        // Sequential retrieval — all three retrievers share the same DbContext / NpgsqlConnection
        // so they cannot run in parallel ("a command is already in progress"). Per-variant
        // vector hits are RRF-fused into a single vector stream before joining BM25 and graph.
        var vectorHits = new List<RankedHit>();
        if (route.UseVectorSearch && route.VectorWeight > 0)
        {
            // Single batched embedding call across all variants.
            var embeddings = await llm.GetEmbeddings()
                .GenerateAsync(variants.ToList(), cancellationToken: ct)
                .ConfigureAwait(false);

            var vectorPerVariant = new List<List<RankedHit>>(variants.Count);
            for (var i = 0; i < variants.Count; i++)
            {
                var queryVector = new Pgvector.Vector(embeddings[i].Vector.ToArray());
                var hits = await VectorSearchAsync(conn, queryVector, effectiveRequest, candidateLimit, ct).ConfigureAwait(false);
                vectorPerVariant.Add(hits);
            }
            vectorHits = RrfFuser.MergeVectorStreams(vectorPerVariant, RrfK);
        }

        var bm25Hits = route.UseBm25Search && route.Bm25Weight > 0
            ? await Bm25SearchAsync(conn, effectiveRequest, candidateLimit, ct).ConfigureAwait(false)
            : new List<RankedHit>();

        var graphRaw = route.UseGraph && route.GraphWeight > 0
            ? await graphRetriever.RetrieveAsync(effectiveRequest.Query, candidateLimit, ct).ConfigureAwait(false)
            : Array.Empty<GraphRetrievalHit>();
        graphRaw = await ApplyNoteFiltersToGraphHitsAsync(graphRaw, effectiveRequest, ct).ConfigureAwait(false);

        var graphHits = graphRaw
            .Select((h, i) => new RankedHit(h.NoteId, h.Content, i + 1, 1.0 - h.Score, h.RelatedEntities, FromVector: false, FromBm25: false))
            .ToList();

        // Optional cross-modal image-vector retrieval — only fires when the embedder
        // (Vertex multimodalembedding) is configured AND there are image_embeddings
        // rows for this project. The text query is embedded in the same multimodal
        // space, then we cosine-search image_embeddings.
        var imageHits = route.UseImageSearch && route.ImageWeight > 0
            ? await ImageVectorSearchAsync(conn, effectiveRequest, candidateLimit, ct).ConfigureAwait(false)
            : new List<RankedHit>();

        var fused = RrfFuser.Fuse(
            vectorHits,
            bm25Hits,
            graphHits,
            imageHits,
            RrfK,
            new RetrievalWeights(route.VectorWeight, route.Bm25Weight, route.GraphWeight, route.ImageWeight));

        // Optional time-decay: re-weight by note age before reranking.
        var decayed = await ApplyTimeDecayAsync(fused, ct).ConfigureAwait(false);

        var reranked = route.UseReranker
            ? await reranker.RerankAsync(effectiveRequest.Query, decayed, ct).ConfigureAwait(false)
            : decayed;
        var top = reranked.Take(effectiveRequest.MaxResults).ToList();

        // Optional abstention check — if the reranker (or fusion when no rerank ran)
        // can't surface a confident hit, signal it explicitly instead of feeding the
        // caller weak retrieves it might mis-interpret as authoritative.
        var abstainOpts = abstentionOptions.Value;
        if (abstainOpts.Enabled)
        {
            string? abstainReason = null;
            if (top.Count == 0)
            {
                abstainReason = fused.Count == 0
                    ? "No candidates matched the query across vector / BM25 / graph."
                    : "All candidates scored below the reranker threshold.";
            }
            else if (top[0].Provenance?.RerankerScore is { } topRer && topRer < abstainOpts.MinTopScore)
            {
                abstainReason = $"Top reranker score {topRer:F2} below confidence threshold {abstainOpts.MinTopScore:F2}.";
            }

            if (abstainReason is not null)
            {
                return new SearchResult(Array.Empty<SearchHit>(), fused.Count, Abstain: true, AbstainReason: abstainReason, Route: routeTrace);
            }
        }

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

        return new SearchResult(top, fused.Count, Route: routeTrace);
    }

    private async Task<IReadOnlyList<string>> BuildQueryVariantsAsync(
        string query,
        QueryRoute route,
        CancellationToken ct)
    {
        var variants = new List<string> { query };

        if (route.Variants is { Count: > 0 })
        {
            variants.AddRange(route.Variants);
        }

        if (route.UseQueryExpansion)
        {
            var expanded = await queryExpander.ExpandAsync(query, ct).ConfigureAwait(false);
            variants.AddRange(expanded);
        }

        return variants
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
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

    private async Task<IReadOnlyList<GraphRetrievalHit>> ApplyNoteFiltersToGraphHitsAsync(
        IReadOnlyList<GraphRetrievalHit> hits,
        SearchRequest request,
        CancellationToken ct)
    {
        if (hits.Count == 0 || !HasNoteFilters(request)) return hits;

        var ids = hits.Select(h => h.NoteId.Value).ToArray();
        var query = db.Notes
            .FromSqlInterpolated($"SELECT * FROM memory.notes WHERE id = ANY({ids})")
            .Where(n => n.SupersededAt == null);

        if (request.Tags is { Count: > 0 })
        {
            query = query.Where(n => n.Tags.Any(t => request.Tags.Contains(t)));
        }
        if (request.Since.HasValue)
        {
            query = query.Where(n => n.CreatedAt >= request.Since.Value);
        }
        if (request.Until.HasValue)
        {
            query = query.Where(n => n.CreatedAt <= request.Until.Value);
        }
        if (request.Kinds is { Count: > 0 })
        {
            query = query.Where(n => request.Kinds.Contains(n.Kind));
        }
        if (request.MemoryTypes is { Count: > 0 })
        {
            query = query.Where(n => request.MemoryTypes.Contains(n.MemoryType));
        }

        var allowed = await query.Select(n => n.Id).ToListAsync(ct).ConfigureAwait(false);
        var allowedSet = allowed.ToHashSet();
        return hits.Where(h => allowedSet.Contains(h.NoteId)).ToArray();
    }

    private static bool HasNoteFilters(SearchRequest request) =>
        request.Tags is { Count: > 0 }
        || request.Since.HasValue
        || request.Until.HasValue
        || request.Kinds is { Count: > 0 }
        || request.MemoryTypes is { Count: > 0 };

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

    private async Task<List<RankedHit>> ImageVectorSearchAsync(
        NpgsqlConnection conn,
        SearchRequest request,
        int limit,
        CancellationToken ct)
    {
        // No embedder configured -> nothing to search.
        if (imageEmbedderHolder.Embedder is not { } embedder) return new List<RankedHit>();

        // Embed the text query in the same multimodal space as image_embeddings.
        // This is the key cross-modal step: 1 query embedding -> cosine vs N image
        // embeddings -> note ids ranked by visual relevance to the query text.
        float[] queryVec;
        try { queryVec = await embedder.EmbedTextAsync(request.Query, ct).ConfigureAwait(false); }
        catch { return new List<RankedHit>(); }

        var pgVec = new Pgvector.Vector(queryVec);
        var sql = new StringBuilder("""
            SELECT n.id,
                   n.content,
                   ie.embedding <=> @qvec AS distance,
                   COALESCE(
                     (SELECT array_agg(m.entity_id)
                      FROM memory.note_entity_mentions m
                      WHERE m.note_id = n.id),
                     ARRAY[]::uuid[]) AS related_entity_ids
            FROM memory.image_embeddings ie
            JOIN memory.notes n ON n.id = ie.note_id
            WHERE n.superseded_at IS NULL
            """);

        await using var cmd = new NpgsqlCommand { Connection = conn };
        cmd.Parameters.AddWithValue("qvec", pgVec);
        cmd.Parameters.AddWithValue("limit", limit);
        AppendFilters(sql, cmd, request);
        sql.Append(" ORDER BY distance ASC LIMIT @limit");
        cmd.CommandText = sql.ToString();

        var hits = new List<RankedHit>();
        var rank = 0;
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rank++;
                var noteId = new NoteId(reader.GetGuid(0));
                var content = reader.GetString(1);
                var distance = reader.GetDouble(2);
                var related = ((Guid[])reader.GetValue(3)).Select(g => new EntityId(g)).ToArray();
                hits.Add(new RankedHit(noteId, content, rank, distance, related, FromVector: false, FromBm25: false));
            }
        }
        catch { /* table empty or query failed — fall through with no image hits */ }
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
        if (request.MemoryTypes is { Count: > 0 })
        {
            sql.Append(" AND n.memory_type = ANY(@memory_types)");
            cmd.Parameters.Add(new NpgsqlParameter("memory_types", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
            {
                Value = request.MemoryTypes.Select(t => (short)t).ToArray(),
            });
        }
    }

    // Fusion math (RRF + per-variant vector merge) lives in RrfFuser.cs so it
    // can be unit-tested in isolation. The pipeline only orchestrates IO.
}
