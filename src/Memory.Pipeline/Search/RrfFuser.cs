using Memory.Domain;

namespace Memory.Pipeline.Search;

/// <summary>
/// Reciprocal Rank Fusion across the four retrieval streams (vector, BM25,
/// graph PPR, image-vector) plus the per-variant vector merge that runs
/// before the four-way fusion. Pulled out of <see cref="HybridSearchPipeline"/>
/// so the math is unit-testable in isolation — the pipeline itself only
/// orchestrates IO, the actual ranking lives here.
///
/// Constant <c>k</c> defaults to 60 in the standard RRF paper; callers can
/// override per call. A note's RRF score is the sum of <c>1 / (k + rank)</c>
/// across every list it appears in. Hits found by multiple retrievers
/// stack contributions — that's the whole point of fusion.
/// </summary>
internal static class RrfFuser
{
    /// <summary>Fuses the four streams into a single ranked list of <see cref="SearchHit"/>.</summary>
    public static List<SearchHit> Fuse(
        List<RankedHit> vector,
        List<RankedHit> bm25,
        List<RankedHit> graph,
        List<RankedHit> image,
        int k)
    {
        var pool = new Dictionary<NoteId, FusedHit>();

        Add(pool, vector, k, stream: Stream.Vector);
        Add(pool, bm25, k, stream: Stream.Bm25);
        Add(pool, graph, k, stream: Stream.Graph);
        Add(pool, image, k, stream: Stream.Image);

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
    /// and re-ranked 1..N before joining the main 4-stream fusion.
    /// </summary>
    public static List<RankedHit> MergeVectorStreams(List<List<RankedHit>> perVariant, int k)
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

    private enum Stream { Vector, Bm25, Graph, Image }

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
                case Stream.Image: existing.FromVector = true; existing.VectorScore = Math.Max(existing.VectorScore, contribution); break;
                    // Image-vector hits are reported under the same FromVector flag because the
                    // SearchHitProvenance wire format doesn't have a dedicated FromImage yet —
                    // both kinds are "embedding-similarity" matches as far as callers care.
                    // A future iteration can expose FromImage + ImageScore if introspection
                    // becomes useful for diagnostics or UI.
            }
        }
    }

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

/// <summary>
/// One hit from a single retrieval stream, with its in-stream rank (1 = best).
/// Shared shape across vector, BM25, graph, and image retrievers — the fuser
/// doesn't care which stream produced what, it just needs a stable ranking.
/// </summary>
internal sealed record RankedHit(
    NoteId NoteId,
    string Content,
    int Rank,
    double Distance,
    EntityId[] Related,
    bool FromVector,
    bool FromBm25);
