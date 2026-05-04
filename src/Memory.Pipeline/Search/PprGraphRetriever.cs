using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Search;

/// <summary>
/// HippoRAG-2-inspired graph-seeded retrieval. The flow:
/// 1. Ask the LLM what entities/concepts the query is about (small, cheap call).
/// 2. Resolve those names against the project's entity table.
/// 3. Run personalized PageRank over the (active-edge) graph, seeded uniformly at the
///    matched entities — this spreads relevance through 1+ hop neighborhoods.
/// 4. Score every note by sum of PPR mass on the entities it mentions.
/// 5. Return the top notes, sorted by that score.
/// The output joins the hybrid pipeline as a third RRF stream (alongside vector + BM25).
/// </summary>
internal sealed class PprGraphRetriever(
    ITenantContext tenant,
    MemoryDbContext db,
    IGraphContext graph,
    ILlmGateway llm,
    IOptions<GraphRetrievalOptions> options,
    ILogger<PprGraphRetriever> logger) : IGraphRetriever
{
    private const string SystemPrompt = """
        You extract entity/concept hints from a user's search query so a knowledge graph
        retriever can seed PageRank at the right places. Return canonical names — short
        nouns or noun phrases, lowercase, hyphen-separated for multi-word names.

        Include the obvious people, projects, places, organizations, and the central
        concepts. Skip stopwords, verbs, generic terms ("things", "stuff"), and the user
        themselves. Up to 8 hints. If the query is too vague or has no specific entities,
        return an empty list.

        Return ONLY a JSON object: { "hints": ["name1", "name2"] }. Do not echo the schema.
        """;

    public async Task<IReadOnlyList<GraphRetrievalHit>> RetrieveAsync(string query, int maxResults, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var opts = options.Value;
        if (!opts.Enabled || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<GraphRetrievalHit>();
        }

        // 1. LLM extracts entity hints from the query.
        var hints = await ExtractHintsAsync(query, ct).ConfigureAwait(false);
        if (hints.Count == 0)
        {
            logger.LogDebug("Graph retriever: no entity hints extracted from query.");
            return Array.Empty<GraphRetrievalHit>();
        }

        // 2. Resolve hints -> EntityIds in this project (case-insensitive substring match).
        var allEntities = await graph.GetEntitiesAsync(scope.Project, limit: 5000, ct: ct).ConfigureAwait(false);
        var seedIds = ResolveSeeds(hints, allEntities, opts.MaxSeeds);
        if (seedIds.Count == 0)
        {
            logger.LogDebug("Graph retriever: hints {Hints} did not resolve to any entity in the project.", string.Join(", ", hints));
            return Array.Empty<GraphRetrievalHit>();
        }

        // 3. Build adjacency from active edges and run PPR.
        var edges = await graph.GetEdgesAsync(scope.Project, validAt: DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);
        var allEntityIds = allEntities.Select(e => e.Id).ToList();
        var ppr = RunPpr(allEntityIds, edges, seedIds, opts.Iterations, opts.Alpha);

        // 4. Score notes by PPR mass on their mentioned entities.
        var mentions = await db.NoteEntityMentions
            .Where(m => m.Project == scope.Project)
            .Select(m => new { m.NoteId, m.EntityId })
            .ToListAsync(ct).ConfigureAwait(false);

        var noteScores = new Dictionary<NoteId, double>();
        var noteEntities = new Dictionary<NoteId, List<EntityId>>();
        foreach (var m in mentions)
        {
            if (!ppr.TryGetValue(m.EntityId, out var mass) || mass <= 0) continue;
            noteScores[m.NoteId] = (noteScores.TryGetValue(m.NoteId, out var existing) ? existing : 0.0) + mass;

            if (!noteEntities.TryGetValue(m.NoteId, out var list))
            {
                list = new List<EntityId>();
                noteEntities[m.NoteId] = list;
            }
            list.Add(m.EntityId);
        }

        if (noteScores.Count == 0) return Array.Empty<GraphRetrievalHit>();

        // 5. Pull content for the top-scoring notes (only active ones).
        var topIds = noteScores
            .OrderByDescending(kv => kv.Value)
            .Take(Math.Min(maxResults, opts.MaxResults))
            .Select(kv => kv.Key)
            .ToList();

        // EF Core can't translate List<NoteId>.Contains for the typed-ID value converter,
        // so query via raw Guid array passed to PG `id = ANY(@ids)`.
        var topGuidArray = topIds.Select(i => i.Value).ToArray();
        var raw = await db.Notes
            .FromSqlInterpolated($"SELECT * FROM memory.notes WHERE superseded_at IS NULL AND id = ANY({topGuidArray})")
            .Select(n => new { n.Id, n.Content })
            .ToListAsync(ct).ConfigureAwait(false);
        var contentLookup = raw.ToDictionary(n => n.Id, n => n.Content);

        var hits = topIds
            .Where(id => contentLookup.ContainsKey(id))
            .Select(id => new GraphRetrievalHit(
                id,
                contentLookup[id],
                noteScores[id],
                noteEntities.TryGetValue(id, out var ents) ? ents.Distinct().ToArray() : Array.Empty<EntityId>()))
            .ToList();

        logger.LogDebug("Graph retriever produced {Count} hits from {SeedCount} seeds.", hits.Count, seedIds.Count);
        return hits;
    }

    private async Task<List<string>> ExtractHintsAsync(string query, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, query),
        };
        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(options.Value.QueryExtractorModel))
        {
            chatOptions.ModelId = options.Value.QueryExtractorModel;
        }

        try
        {
            var response = await llm.GetChat()
                .GetResponseAsync<HintResponse>(messages, options: chatOptions, cancellationToken: ct)
                .ConfigureAwait(false);
            return response.Result?.Hints?
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim().ToLowerInvariant())
                .Distinct()
                .ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Query-entity extraction failed; graph retrieval will return no hits.");
            return new List<string>();
        }
    }

    private static List<EntityId> ResolveSeeds(List<string> hints, IReadOnlyList<Entity> entities, int maxSeeds)
    {
        var seeds = new List<EntityId>();
        var seen = new HashSet<EntityId>();
        foreach (var hint in hints)
        {
            // exact match wins, then substring
            var match = entities.FirstOrDefault(e => string.Equals(e.Name, hint, StringComparison.OrdinalIgnoreCase))
                     ?? entities.FirstOrDefault(e => e.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null && seen.Add(match.Id))
            {
                seeds.Add(match.Id);
                if (seeds.Count >= maxSeeds) break;
            }
        }
        return seeds;
    }

    /// <summary>
    /// Personalized PageRank by power iteration. Treats edges as undirected (each edge contributes
    /// to both directions). Numerically: p_{t+1} = (1-α)·M·p_t + α·s, where M is the row-stochastic
    /// adjacency, s is the seed distribution.
    /// </summary>
    private static Dictionary<EntityId, double> RunPpr(
        IReadOnlyList<EntityId> nodes,
        IReadOnlyList<Edge> edges,
        IReadOnlyList<EntityId> seeds,
        int iterations,
        double alpha)
    {
        if (nodes.Count == 0 || seeds.Count == 0) return new Dictionary<EntityId, double>();

        var index = new Dictionary<EntityId, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++) index[nodes[i]] = i;

        var neighbors = new List<List<int>>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++) neighbors.Add(new List<int>());
        foreach (var e in edges)
        {
            if (e.InvalidatedAt is not null) continue;
            if (!index.TryGetValue(e.From, out var a) || !index.TryGetValue(e.To, out var b)) continue;
            neighbors[a].Add(b);
            neighbors[b].Add(a);
        }

        var seedSet = new HashSet<int>();
        foreach (var s in seeds)
        {
            if (index.TryGetValue(s, out var i)) seedSet.Add(i);
        }
        if (seedSet.Count == 0) return new Dictionary<EntityId, double>();

        var seedMass = 1.0 / seedSet.Count;
        var p = new double[nodes.Count];
        foreach (var i in seedSet) p[i] = seedMass;

        var next = new double[nodes.Count];
        for (var t = 0; t < iterations; t++)
        {
            Array.Clear(next);
            for (var i = 0; i < nodes.Count; i++)
            {
                if (p[i] <= 0) continue;
                var deg = neighbors[i].Count;
                if (deg == 0)
                {
                    next[i] += p[i]; // dangling node — keep mass on self
                    continue;
                }
                var share = p[i] / deg;
                foreach (var j in neighbors[i]) next[j] += share;
            }
            for (var i = 0; i < nodes.Count; i++)
            {
                p[i] = (1.0 - alpha) * next[i] + (seedSet.Contains(i) ? alpha * seedMass : 0.0);
            }
        }

        var result = new Dictionary<EntityId, double>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (p[i] > 0) result[nodes[i]] = p[i];
        }
        return result;
    }

    private sealed record HintResponse(List<string> Hints);
}
