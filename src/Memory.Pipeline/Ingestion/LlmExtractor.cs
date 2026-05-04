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
        Be precise. Return ONLY the structured object — no commentary, no markdown.
        """;

    public async Task<ExtractionResult> ExtractAsync(string content, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, content),
        };

        var response = await llm.GetChat().GetResponseAsync<ExtractionResult>(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Result;
    }
}
