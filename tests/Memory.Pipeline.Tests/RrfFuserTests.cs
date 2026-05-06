using Memory.Domain;
using Memory.Pipeline.Search;

namespace Memory.Pipeline.Tests;

/// <summary>
/// Pure unit tests for the Reciprocal Rank Fusion math. No DB / no LLM —
/// just rank lists in, fused list out. The values verify the documented
/// invariants of the fusion (1/(k+rank) contributions, multi-stream
/// stacking, deterministic ordering by combined score).
/// </summary>
public sealed class RrfFuserTests
{
    private static readonly EntityId[] NoEntities = Array.Empty<EntityId>();
    private const int K = 60;

    [Fact]
    public void Fuse_returns_empty_when_all_streams_empty()
    {
        var fused = RrfFuser.Fuse(
            vector: new(), bm25: new(), graph: new(), image: new(), k: K);

        Assert.Empty(fused);
    }

    [Fact]
    public void Fuse_single_stream_preserves_rank_order()
    {
        var a = NewNote();
        var b = NewNote();
        var c = NewNote();

        // Vector says: a (rank 1), b (rank 2), c (rank 3).
        var vec = new List<RankedHit>
        {
            Hit(a, "alpha", rank: 1),
            Hit(b, "bravo", rank: 2),
            Hit(c, "charlie", rank: 3),
        };

        var fused = RrfFuser.Fuse(vec, new(), new(), new(), K);

        Assert.Equal(3, fused.Count);
        Assert.Equal(a, fused[0].NoteId);
        Assert.Equal(b, fused[1].NoteId);
        Assert.Equal(c, fused[2].NoteId);
        Assert.True(fused[0].Score > fused[1].Score);
        Assert.True(fused[1].Score > fused[2].Score);
    }

    [Fact]
    public void Fuse_stacks_contributions_when_same_note_in_multiple_streams()
    {
        var a = NewNote();
        var b = NewNote();

        // a is rank 2 in vector AND rank 2 in BM25 — should beat b which is
        // only rank 1 in vector. Two contributions of 1/62 = 0.0322 each
        // sum to 0.0645, beating b's single 1/61 = 0.0164.
        var vec = new List<RankedHit>
        {
            Hit(b, "only-vector", rank: 1),
            Hit(a, "both",         rank: 2),
        };
        var bm25 = new List<RankedHit>
        {
            Hit(a, "both", rank: 2),
        };

        var fused = RrfFuser.Fuse(vec, bm25, new(), new(), K);

        Assert.Equal(a, fused[0].NoteId);
        Assert.Equal(b, fused[1].NoteId);
        Assert.True(fused[0].Provenance!.FromVector);
        Assert.True(fused[0].Provenance!.FromBm25);
        Assert.False(fused[0].Provenance!.FromGraph);
    }

    [Fact]
    public void Fuse_provenance_records_which_streams_hit()
    {
        var a = NewNote();

        var fused = RrfFuser.Fuse(
            vector: new() { Hit(a, "v", 1) },
            bm25: new() { Hit(a, "v", 5) },
            graph: new() { Hit(a, "v", 10) },
            image: new(),
            k: K);

        var hit = Assert.Single(fused);
        Assert.NotNull(hit.Provenance);
        Assert.True(hit.Provenance!.FromVector);
        Assert.True(hit.Provenance!.FromBm25);
        Assert.True(hit.Provenance!.FromGraph);
        Assert.True(hit.Provenance!.VectorScore > 0);
        Assert.True(hit.Provenance!.Bm25Score > 0);
        Assert.True(hit.Provenance!.GraphScore > 0);
        Assert.Null(hit.Provenance!.RerankerScore); // reranker runs later
    }

    [Fact]
    public void Fuse_image_hits_register_under_FromVector_with_max_score()
    {
        var a = NewNote();

        // text-vector at rank 5, image-vector at rank 1 — same note, both
        // are "embedding similarity." The image score is higher (better rank);
        // VectorScore should reflect the max of the two contributions.
        var textVec = new List<RankedHit> { Hit(a, "v", 5) };
        var imageVec = new List<RankedHit> { Hit(a, "v", 1) };

        var fused = RrfFuser.Fuse(textVec, new(), new(), imageVec, K);

        var hit = Assert.Single(fused);
        Assert.True(hit.Provenance!.FromVector);
        var textContribution = 1.0 / (K + 5);
        var imageContribution = 1.0 / (K + 1);
        Assert.Equal(Math.Max(textContribution, imageContribution), hit.Provenance!.VectorScore, precision: 6);
    }

    [Fact]
    public void Fuse_total_score_equals_sum_of_per_stream_contributions()
    {
        var a = NewNote();

        var fused = RrfFuser.Fuse(
            vector: new() { Hit(a, "v", 1) },
            bm25: new() { Hit(a, "v", 2) },
            graph: new() { Hit(a, "v", 3) },
            image: new(),
            k: K);

        var hit = Assert.Single(fused);
        var expected = 1.0 / (K + 1) + 1.0 / (K + 2) + 1.0 / (K + 3);
        Assert.Equal(expected, hit.Score, precision: 6);
    }

    [Fact]
    public void MergeVectorStreams_returns_sole_list_unchanged_for_single_variant()
    {
        var a = NewNote();
        var single = new List<List<RankedHit>>
        {
            new() { Hit(a, "v", 1) },
        };

        var merged = RrfFuser.MergeVectorStreams(single, K);

        Assert.Same(single[0], merged);
    }

    [Fact]
    public void MergeVectorStreams_reranks_by_combined_RRF_score()
    {
        var a = NewNote();
        var b = NewNote();
        var c = NewNote();

        // Variant 1: a@1, b@2, c@3
        // Variant 2: c@1, a@2 (b absent)
        // Combined: a = 1/61 + 1/62, b = 1/62, c = 1/63 + 1/61
        // a beats c (61+62 > 63+61), c beats b.
        var perVariant = new List<List<RankedHit>>
        {
            new() { Hit(a, "alpha", 1), Hit(b, "bravo", 2), Hit(c, "charlie", 3) },
            new() { Hit(c, "charlie", 1), Hit(a, "alpha", 2) },
        };

        var merged = RrfFuser.MergeVectorStreams(perVariant, K);

        Assert.Equal(3, merged.Count);
        Assert.Equal(a, merged[0].NoteId);
        Assert.Equal(c, merged[1].NoteId);
        Assert.Equal(b, merged[2].NoteId);
        // Re-ranking: 1..N
        Assert.Equal(1, merged[0].Rank);
        Assert.Equal(2, merged[1].Rank);
        Assert.Equal(3, merged[2].Rank);
    }

    [Fact]
    public void MergeVectorStreams_handles_disjoint_variants()
    {
        var a = NewNote();
        var b = NewNote();

        // Two variants with no overlap — merged stream has both, ordered by
        // their own per-variant rank (both rank 1 → tie broken by dict order,
        // but the size and re-ranking must be correct).
        var perVariant = new List<List<RankedHit>>
        {
            new() { Hit(a, "alpha", 1) },
            new() { Hit(b, "bravo", 1) },
        };

        var merged = RrfFuser.MergeVectorStreams(perVariant, K);

        Assert.Equal(2, merged.Count);
        Assert.Equal(1, merged[0].Rank);
        Assert.Equal(2, merged[1].Rank);
    }

    private static NoteId NewNote() => new(Guid.NewGuid());

    private static RankedHit Hit(NoteId id, string content, int rank) =>
        new(id, content, rank, Distance: 1.0 - 1.0 / rank,
            Related: NoEntities, FromVector: false, FromBm25: false);
}
