using Memory.Llm;
using Microsoft.Extensions.AI;

namespace Memory.Pipeline.Ingestion;

internal sealed class LlmExtractor(ILlmGateway llm) : IExtractor
{
    private const string SystemPrompt = """
        You distill the user's text into a structured memory record. Extract:
        - One Zettelkasten-style atomic note (a single self-contained insight, 1-3 sentences) capturing
          the most important takeaway from the input.
        - A short context description for the note (under 200 chars) — what kind of source it came from
          and the situation around it.
        - 3-8 keywords: lowercase nouns or short phrases that summarize the note.
        - 0-5 tags: broad categories, lowercase, hyphen-separated if multi-word.
        - All entities mentioned (people, projects, concepts, places, organizations) with kind + attributes.
        - Relationships between entities (e.g. WORKS_AT, MENTIONS, AUTHORED, KNOWS, USES) with optional properties.

        Use canonical entity names (lowercase, hyphen-separated for multi-word).

        IMPORTANT: All attribute and property VALUES must be strings. Convert booleans, numbers, and dates
        into their string representations (e.g. "true", "1815", "2024-03-15"). Never emit raw booleans,
        numbers, or null inside the attributes/properties dictionaries.

        Return ONLY a JSON object with the actual values populated. Do NOT echo back the schema definition
        (no "type", "properties", "items" wrapper keys — those are schema metadata, not values).
        """;

    public async Task<ExtractionResult> ExtractAsync(string content, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, content),
        };

        var response = await llm.GetChat().GetResponseAsync<ExtractionResult>(messages, cancellationToken: ct).ConfigureAwait(false);

        if (response.Result is null || response.Result.Note is null)
        {
            var raw = response.Text ?? "(no text content in response)";
            var preview = raw.Length > 800 ? raw[..800] + "..." : raw;
            throw new InvalidOperationException(
                $"LLM did not return a parseable ExtractionResult (Note is null). " +
                $"Provider may not support strict JSON-schema response_format, or output was malformed. " +
                $"Raw response preview: {preview}");
        }

        return response.Result;
    }
}
