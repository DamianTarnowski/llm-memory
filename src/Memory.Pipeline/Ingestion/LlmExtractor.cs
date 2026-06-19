using Memory.Llm;
using Microsoft.Extensions.AI;

namespace Memory.Pipeline.Ingestion;

internal sealed class LlmExtractor(ILlmGateway llm) : IExtractor
{
    private const string SystemPrompt = """
        You distill the user's text into a structured memory record. Extract:
        - notes: ONE OR MORE Zettelkasten-style atomic notes. Short input -> 1 note. Long or
          multi-topic input (a long doc, a meeting transcript, a multi-paragraph reflection) ->
          split into 2-5 atomic notes, EACH a single self-contained insight in 1-3 sentences. Do
          NOT recap the whole input as one note when it covers distinct ideas — split it.
          Each note has: content, contextDescription (under 200 chars), 3-8 keywords (lowercase),
          0-5 tags (lowercase, hyphen-separated), a kind (one of: General, Observation,
          Decision, Learning, Error, Pattern), and a memoryType (one of: Semantic, Episodic,
          Procedural, Preference, Document, Reflection). Pick the kind that best matches:
            * Observation — factual statement about current state, no decision implied
            * Decision    — a choice made with the reasoning attached
            * Learning    — a lesson distilled from experience ("I learned X")
            * Error       — a bug, mistake, gotcha, or what-not-to-do
            * Pattern     — a repeating pattern, heuristic, principle, rule-of-thumb
            * General     — fallback when none of the above clearly applies
          Pick memoryType by how future agents should use the note:
            * Semantic    — durable facts about projects, systems, entities, or concepts
            * Episodic    — events, sessions, deploys, incidents, and time-bound observations
            * Procedural  — workflows, coding patterns, checklists, and how-to knowledge
            * Preference  — user/team preferences, habits, corrections, or "do not do this again"
            * Document    — imported documents, source chunks, blob references, or quoted material
            * Reflection  — consolidated summaries and higher-level retrospectives
        - entities: all entities mentioned (people, projects, concepts, places, organizations)
          with kind + attributes.
        - relationships: between entities (e.g. WORKS_AT, MENTIONS, AUTHORED, KNOWS, USES) with
          optional properties.
        - supersedesPriorEdges: when the input states that a previously-true relation no longer
          holds (e.g. "X used to work at Y, now works at Z" -> mark X-WORKS_AT-Y as superseded;
          "She moved from London to Tokyo" -> mark her-LIVES_IN-london as superseded). List the
          OLD (from, to, relation) triples here. Skip when there's no past-vs-present contrast.

        Use canonical entity names (lowercase, hyphen-separated for multi-word).

        IMPORTANT: All attribute and property VALUES must be strings. Convert booleans, numbers, and
        dates into their string representations. Never emit raw booleans/numbers/null inside the
        attributes/properties dictionaries.

        Return ONLY a JSON object with the actual values populated. Do NOT echo back the schema
        definition (no "type", "properties", "items" wrapper keys — those are schema metadata).
        """;

    public async Task<ExtractionResult> ExtractAsync(string content, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, content),
        };

        var response = await llm.GetChat().GetResponseAsync<ExtractionResult>(messages, cancellationToken: ct).ConfigureAwait(false);

        if (response.Result is null || response.Result.Notes is not { Count: > 0 } notes || notes.Any(n => string.IsNullOrWhiteSpace(n.Content)))
        {
            var raw = response.Text ?? "(no text content in response)";
            var preview = raw.Length > 800 ? raw[..800] + "..." : raw;
            throw new InvalidOperationException(
                $"LLM did not return a parseable ExtractionResult (Notes empty or has null Content). " +
                $"Provider may not support strict JSON-schema response_format, or output was malformed. " +
                $"Raw response preview: {preview}");
        }

        return response.Result;
    }
}
