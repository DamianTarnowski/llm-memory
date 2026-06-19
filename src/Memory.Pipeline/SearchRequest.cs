using Memory.Domain;

namespace Memory.Pipeline;

public sealed record SearchRequest(
    string Query,
    int MaxResults = 20,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    IReadOnlyList<NoteKind>? Kinds = null,
    /// <summary>
    /// When set, hits are packed greedily by descending score until the
    /// estimated token cost of their content exceeds this budget. Useful for
    /// agent integration where the consumer cares about context window, not
    /// row count. Token estimate is conservative (chars / 3.8) — better to
    /// underfill than to blow the model's window.
    /// </summary>
    int? MaxTokens = null,
    SearchContext? Context = null,
    QueryRoute? RouteOverride = null,
    IReadOnlyList<MemoryType>? MemoryTypes = null);

public sealed record SearchContext(
    string? Caller = null,
    string? Model = null,
    string? ActiveProject = null,
    IReadOnlyList<string>? AllowedScopes = null,
    string? ConversationSummary = null,
    IReadOnlyList<string>? RecentTurns = null,
    string? CurrentTopic = null,
    IReadOnlyList<string>? LastHitIds = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record SearchHit(
    NoteId NoteId,
    string Content,
    double Score,
    IReadOnlyList<EntityId> RelatedEntities,
    SearchHitProvenance? Provenance = null);

public sealed record SearchHitProvenance(
    bool FromVector,
    bool FromBm25,
    bool FromGraph,
    double VectorScore,
    double Bm25Score,
    double GraphScore,
    double? RerankerScore);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    int TotalCandidates,
    /// <summary>
    /// True when the pipeline judges no hit relevant enough to surface.
    /// LongMemEval treats abstention ("I don't know") as a core memory ability
    /// — it's better to admit there's no good answer than to surface a weak
    /// one and have the caller / agent confidently misuse it.
    /// </summary>
    bool Abstain = false,
    string? AbstainReason = null,
    SearchRouteTrace? Route = null);

public enum SearchMode
{
    NoRag,
    MemoryLight,
    MemoryMedium,
    HeavyRag,
    GraphRag,
    DocumentRag,
    WriteMemory,
}

public sealed record QueryRoute(
    bool ShouldSearch,
    SearchMode Mode,
    string StandaloneQuery,
    string QueryType = "general",
    int MaxResults = 20,
    IReadOnlyList<string>? Variants = null,
    bool UseVectorSearch = true,
    bool UseBm25Search = true,
    bool UseGraph = true,
    bool UseReranker = true,
    bool UseQueryExpansion = true,
    bool UseImageSearch = true,
    double VectorWeight = 1.0,
    double Bm25Weight = 1.0,
    double GraphWeight = 1.0,
    double ImageWeight = 1.0,
    string? RouterModel = null,
    double? Confidence = null,
    string? SkipReason = null,
    IReadOnlyDictionary<string, string>? BlobFilters = null)
{
    public static QueryRoute Default(SearchRequest request)
    {
        var query = request.Query.Trim();
        return new QueryRoute(
            ShouldSearch: true,
            Mode: SearchMode.MemoryMedium,
            StandaloneQuery: query,
            MaxResults: request.MaxResults);
    }

    public static SearchMode ParseMode(string? mode)
    {
        var normalized = (mode ?? "")
            .Trim()
            .Replace("-", "_", StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "no_rag" or "none" or "skip" => SearchMode.NoRag,
            "memory_light" or "light" => SearchMode.MemoryLight,
            "memory_medium" or "medium" or "memory" => SearchMode.MemoryMedium,
            "heavy_rag" or "heavy" => SearchMode.HeavyRag,
            "graph_rag" or "graph" => SearchMode.GraphRag,
            "document_rag" or "doc_rag" or "documents" or "blob" => SearchMode.DocumentRag,
            "write_memory" or "write" or "save" => SearchMode.WriteMemory,
            _ => SearchMode.MemoryMedium,
        };
    }

    public QueryRoute Normalize(SearchRequest request, int maxResultsCap = 50)
    {
        var original = request.Query.Trim();
        var standalone = string.IsNullOrWhiteSpace(StandaloneQuery) ? original : StandaloneQuery.Trim();
        var maxResults = Math.Clamp(MaxResults <= 0 ? request.MaxResults : MaxResults, 1, Math.Clamp(maxResultsCap, 1, 100));

        var variants = (Variants ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Where(v => !string.Equals(v, standalone, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        return this with
        {
            StandaloneQuery = standalone,
            QueryType = string.IsNullOrWhiteSpace(QueryType) ? "general" : QueryType.Trim(),
            MaxResults = maxResults,
            Variants = variants,
            VectorWeight = ClampWeight(VectorWeight),
            Bm25Weight = ClampWeight(Bm25Weight),
            GraphWeight = ClampWeight(GraphWeight),
            ImageWeight = ClampWeight(ImageWeight),
            Confidence = Confidence.HasValue ? Math.Clamp(Confidence.Value, 0.0, 1.0) : null,
        };
    }

    public SearchRouteTrace ToTrace(string originalQuery, IReadOnlyList<string> effectiveVariants) => new(
        OriginalQuery: originalQuery,
        StandaloneQuery: StandaloneQuery,
        Mode: Mode.ToString(),
        QueryType: QueryType,
        ShouldSearch: ShouldSearch,
        QueryVariants: effectiveVariants,
        MaxResults: MaxResults,
        UseVectorSearch: UseVectorSearch,
        UseBm25Search: UseBm25Search,
        UseGraph: UseGraph,
        UseReranker: UseReranker,
        UseQueryExpansion: UseQueryExpansion,
        UseImageSearch: UseImageSearch,
        VectorWeight: VectorWeight,
        Bm25Weight: Bm25Weight,
        GraphWeight: GraphWeight,
        ImageWeight: ImageWeight,
        RouterModel: RouterModel,
        Confidence: Confidence,
        SkipReason: SkipReason,
        BlobFilters: BlobFilters);

    private static double ClampWeight(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 1.0;
        return Math.Clamp(value, 0.0, 2.0);
    }
}

public sealed record SearchRouteTrace(
    string OriginalQuery,
    string StandaloneQuery,
    string Mode,
    string QueryType,
    bool ShouldSearch,
    IReadOnlyList<string> QueryVariants,
    int MaxResults,
    bool UseVectorSearch,
    bool UseBm25Search,
    bool UseGraph,
    bool UseReranker,
    bool UseQueryExpansion,
    bool UseImageSearch,
    double VectorWeight,
    double Bm25Weight,
    double GraphWeight,
    double ImageWeight,
    string? RouterModel,
    double? Confidence,
    string? SkipReason,
    IReadOnlyDictionary<string, string>? BlobFilters);
