using Memory.Pipeline;

namespace Memory.Pipeline.Tests;

public sealed class QueryRouteTests
{
    [Fact]
    public void Default_route_preserves_query_and_requested_max_results()
    {
        var request = new SearchRequest("  test memory routing  ", MaxResults: 7);

        var route = QueryRoute.Default(request);

        Assert.True(route.ShouldSearch);
        Assert.Equal(SearchMode.MemoryMedium, route.Mode);
        Assert.Equal("test memory routing", route.StandaloneQuery);
        Assert.Equal(7, route.MaxResults);
        Assert.True(route.UseVectorSearch);
        Assert.True(route.UseBm25Search);
        Assert.True(route.UseGraph);
    }

    [Fact]
    public void Normalize_clamps_weights_results_and_variants()
    {
        var request = new SearchRequest("original query", MaxResults: 20);
        var route = new QueryRoute(
            ShouldSearch: true,
            Mode: SearchMode.HeavyRag,
            StandaloneQuery: "",
            MaxResults: 500,
            Variants: new[] { "variant a", "variant a", " ", "variant b" },
            VectorWeight: -5,
            Bm25Weight: 9,
            GraphWeight: double.NaN,
            ImageWeight: 0.5,
            Confidence: 4.2);

        var normalized = route.Normalize(request, maxResultsCap: 30);

        Assert.Equal("original query", normalized.StandaloneQuery);
        Assert.Equal(30, normalized.MaxResults);
        Assert.Equal(new[] { "variant a", "variant b" }, normalized.Variants);
        Assert.Equal(0, normalized.VectorWeight);
        Assert.Equal(2, normalized.Bm25Weight);
        Assert.Equal(1, normalized.GraphWeight);
        Assert.Equal(0.5, normalized.ImageWeight);
        Assert.Equal(1, normalized.Confidence);
    }

    [Fact]
    public void ToTrace_captures_router_decision()
    {
        var route = new QueryRoute(
            ShouldSearch: false,
            Mode: SearchMode.NoRag,
            StandaloneQuery: "hello",
            QueryType: "chitchat",
            SkipReason: "greeting");

        var trace = route.ToTrace("hej", Array.Empty<string>());

        Assert.Equal("hej", trace.OriginalQuery);
        Assert.Equal("hello", trace.StandaloneQuery);
        Assert.Equal(nameof(SearchMode.NoRag), trace.Mode);
        Assert.False(trace.ShouldSearch);
        Assert.Equal("greeting", trace.SkipReason);
    }
}
