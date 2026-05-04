using Memory.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Search;

internal sealed class LlmQueryExpander(
    ILlmGateway llm,
    IOptions<QueryExpansionOptions> options,
    ILogger<LlmQueryExpander> logger) : IQueryExpander
{
    private const string SystemPrompt = """
        You generate alternative phrasings of a short search query so a vector retriever can
        find more matches. Return paraphrases and natural-language expansions of plausible
        meanings — same intent, different wording.

        Rules:
        - DO NOT broaden the topic. "AGE" -> ["Apache AGE graph extension", "Cypher queries
          on AGE", "AGE for postgres"], not ["Age of Empires"].
        - Each variant should be a self-contained noun phrase or short sentence the user
          might also have written. Lowercase.
        - If the original is already specific and contains 5+ meaningful words, return [].

        Return ONLY a JSON object: { "variants": ["v1", "v2", "v3"] }. Do not echo the schema.
        """;

    public async Task<IReadOnlyList<string>> ExpandAsync(string original, CancellationToken ct = default)
    {
        var opts = options.Value;
        var trimmed = original.Trim();
        if (!opts.Enabled || string.IsNullOrEmpty(trimmed))
        {
            return new[] { trimmed };
        }

        var wordCount = trimmed.Split(' ', '\t', '\n', '\r').Count(w => !string.IsNullOrEmpty(w));
        if (wordCount > opts.MaxQueryWords)
        {
            return new[] { trimmed };
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, trimmed),
        };
        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(opts.ModelOverride))
        {
            chatOptions.ModelId = opts.ModelOverride;
        }

        try
        {
            var response = await llm.GetChat()
                .GetResponseAsync<VariantResponse>(messages, options: chatOptions, cancellationToken: ct)
                .ConfigureAwait(false);

            var variants = response.Result?.Variants?
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Where(v => !string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase))
                .Take(opts.VariantCount)
                .ToList() ?? new List<string>();

            var all = new List<string>(variants.Count + 1) { trimmed };
            all.AddRange(variants);
            return all;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Query expansion failed; falling back to original query.");
            return new[] { trimmed };
        }
    }

    private sealed record VariantResponse(List<string> Variants);
}
