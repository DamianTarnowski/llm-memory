using System.Text;
using System.Text.Json.Serialization;
using Memory.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Search;

internal sealed class LlmQueryRouter(
    ILlmGateway llm,
    IOptions<QueryRoutingOptions> options,
    ILogger<LlmQueryRouter> logger) : IQueryRouter
{
    private const string SystemPrompt = """
        You are a routing layer for a shared, multi-agent memory system.
        Your job is NOT to answer the user. Return only routing decisions.

        The memory system stores personal memories, project decisions, coding patterns,
        runbooks, ideas, conversation episodes, graph entities and future document/blob
        chunks. The caller may be Codex, Claude Code, DevHub/Opus or another agent.

        Decide whether retrieval is needed and choose a mode:
        - no_rag: greetings, meta questions, pure commands that do not need memory search
        - memory_light: simple lookup, few results, no reranker needed
        - memory_medium: normal hybrid memory search
        - heavy_rag: vague follow-up or complex question needing rewrite, expansion and rerank
        - graph_rag: relations between people/projects/apps/decisions/entities
        - document_rag: query appears to need stored documents/blob chunks
        - write_memory: user wants to save/remember something; do not search

        Use standalone_query to resolve follow-ups like "powiedz o tym więcej" into a
        self-contained query using the provided recent context. If there is no context,
        keep the original query.

        Weight fields are relative multipliers for retrieval streams:
        - 1.0 means normal/default
        - 0.0 disables that stream
        - 0.2-0.6 de-emphasizes
        - 1.2-1.5 emphasizes
        Prefer bm25 for exact names, errors, ids, file names and dates.
        Prefer vector for semantic questions and vague descriptions.
        Prefer graph for relation/dependency questions.

        Return ONLY JSON matching this shape:
        {
          "should_search": true,
          "mode": "memory_medium",
          "standalone_query": "...",
          "query_type": "general|follow_up|factual|semantic|relational|architecture|coding_pattern|document|write|chitchat",
          "max_results": 3,
          "variants": ["optional alternate query"],
          "use_vector_search": true,
          "use_bm25_search": true,
          "use_graph": false,
          "use_reranker": true,
          "use_query_expansion": true,
          "use_image_search": false,
          "vector_weight": 1.0,
          "bm25_weight": 1.0,
          "graph_weight": 1.0,
          "image_weight": 1.0,
          "confidence": 0.8,
          "skip_reason": null,
          "blob_filters": { "content_type": "application/pdf" }
        }
        """;

    public async Task<QueryRoute> RouteAsync(SearchRequest request, CancellationToken ct = default)
    {
        if (request.RouteOverride is { } routeOverride)
        {
            return Normalize(routeOverride, request);
        }

        var opts = options.Value;
        if (!opts.Enabled)
        {
            return QueryRoute.Default(request);
        }

        var trimmed = request.Query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return QueryRoute.Default(request);
        }

        try
        {
            var response = await llm.GetChat()
                .GetResponseAsync<RouterResponse>(
                    BuildMessages(request, opts),
                    options: BuildChatOptions(opts),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            if (response.Result is not { } routed)
            {
                return QueryRoute.Default(request);
            }

            var route = new QueryRoute(
                ShouldSearch: routed.ShouldSearch ?? true,
                Mode: QueryRoute.ParseMode(routed.Mode),
                StandaloneQuery: string.IsNullOrWhiteSpace(routed.StandaloneQuery) ? trimmed : routed.StandaloneQuery!,
                QueryType: routed.QueryType ?? "general",
                MaxResults: routed.MaxResults ?? request.MaxResults,
                Variants: routed.Variants?.Take(Math.Max(0, opts.MaxVariants)).ToArray(),
                UseVectorSearch: routed.UseVectorSearch ?? true,
                UseBm25Search: routed.UseBm25Search ?? true,
                UseGraph: routed.UseGraph ?? true,
                UseReranker: routed.UseReranker ?? true,
                UseQueryExpansion: routed.UseQueryExpansion ?? true,
                UseImageSearch: routed.UseImageSearch ?? true,
                VectorWeight: routed.VectorWeight ?? 1.0,
                Bm25Weight: routed.Bm25Weight ?? 1.0,
                GraphWeight: routed.GraphWeight ?? 1.0,
                ImageWeight: routed.ImageWeight ?? 1.0,
                RouterModel: opts.ModelOverride,
                Confidence: routed.Confidence,
                SkipReason: routed.SkipReason,
                BlobFilters: routed.BlobFilters);

            return Normalize(route, request);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Query routing failed; falling back to default memory search.");
            return QueryRoute.Default(request);
        }
    }

    private QueryRoute Normalize(QueryRoute route, SearchRequest request)
    {
        var opts = options.Value;
        var normalized = route.Normalize(request, opts.MaxResultsCap);

        if (normalized.Mode == SearchMode.DocumentRag && !opts.AllowDocumentRag)
        {
            normalized = normalized with
            {
                Mode = SearchMode.HeavyRag,
                QueryType = string.IsNullOrWhiteSpace(normalized.QueryType) ? "document" : normalized.QueryType,
                UseGraph = normalized.UseGraph,
                SkipReason = normalized.SkipReason ?? "document_rag requested but document/blob retriever is not enabled; using memory search fallback",
            };
        }

        return normalized.Mode switch
        {
            SearchMode.NoRag => normalized with
            {
                ShouldSearch = false,
                UseVectorSearch = false,
                UseBm25Search = false,
                UseGraph = false,
                UseReranker = false,
                UseQueryExpansion = false,
                UseImageSearch = false,
                SkipReason = normalized.SkipReason ?? "router selected no_rag",
            },
            SearchMode.WriteMemory => normalized with
            {
                ShouldSearch = false,
                UseVectorSearch = false,
                UseBm25Search = false,
                UseGraph = false,
                UseReranker = false,
                UseQueryExpansion = false,
                UseImageSearch = false,
                SkipReason = normalized.SkipReason ?? "router selected write_memory",
            },
            SearchMode.MemoryLight => normalized with
            {
                UseGraph = false,
                UseReranker = false,
                UseQueryExpansion = false,
                UseImageSearch = false,
                MaxResults = Math.Min(normalized.MaxResults, 5),
            },
            SearchMode.GraphRag => normalized with
            {
                UseGraph = true,
                GraphWeight = Math.Max(normalized.GraphWeight, 1.0),
            },
            SearchMode.HeavyRag => normalized with
            {
                UseReranker = true,
                UseQueryExpansion = true,
            },
            _ => normalized,
        };
    }

    private static ChatOptions BuildChatOptions(QueryRoutingOptions opts)
    {
        var chatOptions = new ChatOptions();
        if (!string.IsNullOrWhiteSpace(opts.ModelOverride))
        {
            chatOptions.ModelId = opts.ModelOverride;
        }
        return chatOptions;
    }

    private static List<ChatMessage> BuildMessages(SearchRequest request, QueryRoutingOptions opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Current user query:");
        sb.AppendLine(Trim(request.Query, 1200));

        if (request.Context is { } ctx)
        {
            Append(sb, "Caller", ctx.Caller);
            Append(sb, "Model", ctx.Model);
            Append(sb, "Active project", ctx.ActiveProject);
            AppendList(sb, "Allowed scopes", ctx.AllowedScopes, 12, 120);
            Append(sb, "Current topic", ctx.CurrentTopic);
            Append(sb, "Conversation summary", ctx.ConversationSummary, 1200);
            AppendList(sb, "Recent turns", ctx.RecentTurns, opts.MaxRecentTurns, 500);
            AppendList(sb, "Last memory hit ids", ctx.LastHitIds, 10, 120);
        }

        var userPrompt = Trim(sb.ToString(), Math.Max(1000, opts.MaxPromptChars));
        return new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userPrompt),
        };
    }

    private static void Append(StringBuilder sb, string label, string? value, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(Trim(value, maxChars));
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string>? values, int maxItems, int maxChars)
    {
        if (values is not { Count: > 0 }) return;
        sb.AppendLine(label + ":");
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(maxItems))
        {
            sb.Append("- ").AppendLine(Trim(value, maxChars));
        }
    }

    private static string Trim(string value, int maxChars)
    {
        value = value.Trim();
        return value.Length <= maxChars ? value : value[..maxChars] + "...";
    }

    private sealed record RouterResponse(
        [property: JsonPropertyName("should_search")] bool? ShouldSearch,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("standalone_query")] string? StandaloneQuery,
        [property: JsonPropertyName("query_type")] string? QueryType,
        [property: JsonPropertyName("max_results")] int? MaxResults,
        [property: JsonPropertyName("variants")] List<string>? Variants,
        [property: JsonPropertyName("use_vector_search")] bool? UseVectorSearch,
        [property: JsonPropertyName("use_bm25_search")] bool? UseBm25Search,
        [property: JsonPropertyName("use_graph")] bool? UseGraph,
        [property: JsonPropertyName("use_reranker")] bool? UseReranker,
        [property: JsonPropertyName("use_query_expansion")] bool? UseQueryExpansion,
        [property: JsonPropertyName("use_image_search")] bool? UseImageSearch,
        [property: JsonPropertyName("vector_weight")] double? VectorWeight,
        [property: JsonPropertyName("bm25_weight")] double? Bm25Weight,
        [property: JsonPropertyName("graph_weight")] double? GraphWeight,
        [property: JsonPropertyName("image_weight")] double? ImageWeight,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("skip_reason")] string? SkipReason,
        [property: JsonPropertyName("blob_filters")] Dictionary<string, string>? BlobFilters);
}
