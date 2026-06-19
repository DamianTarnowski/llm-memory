using Memory.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Search;

internal sealed class LlmReranker(
    ILlmGateway llm,
    IOptions<RerankerOptions> options,
    ILogger<LlmReranker> logger) : IReranker
{
    private const string SystemPrompt = """
        You score how well each candidate note answers the user's query. For each candidate index,
        return a relevance score in [0.0, 1.0]:
        - 1.0  perfect answer / exactly the asked-for fact
        - 0.7  closely related, useful context
        - 0.4  tangentially related
        - 0.1  weakly related (mentions a shared word but answers a different question)
        - 0.0  irrelevant

        Be strict — many candidates will score below 0.4. Return ONLY valid JSON; do NOT echo the
        schema definition.
        """;

    public async Task<IReadOnlyList<SearchHit>> RerankAsync(
        string query,
        IReadOnlyList<SearchHit> candidates,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled || candidates.Count == 0) return candidates;

        var top = candidates.Take(opts.TopN).ToList();

        var prompt = BuildPrompt(query, top, opts.MaxCharsPerCandidate);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, prompt),
        };

        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(opts.ModelOverride))
        {
            chatOptions.ModelId = opts.ModelOverride;
        }

        try
        {
            var response = await llm.GetChat()
                .GetResponseAsync<RerankResponse>(messages, options: chatOptions, cancellationToken: ct)
                .ConfigureAwait(false);

            if (response.Result?.Scores is not { Count: > 0 } scores)
            {
                logger.LogDebug("Reranker returned no scores; falling back to RRF order.");
                return candidates;
            }

            var lookup = scores
                .Where(s => s.Index >= 0 && s.Index < top.Count)
                .ToDictionary(s => s.Index, s => s.Score);

            var reranked = top
                .Select((hit, idx) => new
                {
                    hit,
                    score = lookup.TryGetValue(idx, out var s) ? s : 0.0,
                })
                .Where(x => x.score >= opts.MinRelevance)
                .OrderByDescending(x => x.score)
                .Select(x => new SearchHit(
                    x.hit.NoteId,
                    x.hit.Content,
                    x.score,
                    x.hit.RelatedEntities,
                    x.hit.Provenance is { } p
                        ? p with { RerankerScore = x.score }
                        : new SearchHitProvenance(false, false, false, 0, 0, 0, x.score)))
                .ToList();

            return reranked;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM reranker failed; falling back to RRF order.");
            return candidates;
        }
    }

    private static string BuildPrompt(string query, IReadOnlyList<SearchHit> candidates, int maxChars)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Query: ").Append(query).Append("\n\nCandidates:\n");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i].Content;
            if (c.Length > maxChars) c = c[..maxChars] + "…";
            sb.Append('[').Append(i).Append("] ").Append(c).Append('\n');
        }
        return sb.ToString();
    }

    public sealed record RerankResponse(List<RerankScore> Scores);
    public sealed record RerankScore(int Index, double Score);
}
