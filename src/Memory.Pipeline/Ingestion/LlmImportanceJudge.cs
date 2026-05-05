using Memory.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Ingestion;

internal sealed class LlmImportanceJudge(
    ILlmGateway llm,
    IOptions<SaveFilterOptions> options,
    ILogger<LlmImportanceJudge> logger) : IImportanceJudge
{
    private const string SystemPrompt = """
        You are guarding a personal long-term memory store from low-signal noise. Score how
        worth-remembering an incoming text is on a 0.0-1.0 scale.

        High signal (0.7-1.0): durable facts about people / projects / decisions / preferences,
        learnings, commitments, goals, lessons-learned, technical findings worth recalling later.

        Medium signal (0.3-0.7): contextual information that's useful but not unique — meeting
        recaps, status updates, ongoing-work notes.

        Low signal (0.0-0.3): chitchat, transient mood, single-keystroke smashes, duplicates of
        the literal text just sent, debug pastes without insight, content that's purely a question
        without an answer, anything that won't be useful to retrieve a month from now.

        Output ONLY a JSON object: { "score": 0.0-1.0, "save": true|false, "reason": "<one short sentence>" }.
        Set save=false when score < 0.3 OR the text is structurally unsavable (e.g. empty, a single
        emoji, an obvious test/debug ping). Otherwise save=true.
        """;

    public async Task<ImportanceJudgment> JudgeAsync(string source, string content, CancellationToken ct = default)
    {
        var opts = options.Value;
        // Defensive default — if the LLM call fails, we save (fail-open) so users don't lose data.
        var fallback = new ImportanceJudgment(1.0, true, "judge unavailable; saving by default");

        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(opts.ModelOverride))
        {
            chatOptions.ModelId = opts.ModelOverride;
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"source: {source}\n\n{content}"),
        };

        try
        {
            var response = await llm.GetChat()
                .GetResponseAsync<ImportanceJudgment>(messages, options: chatOptions, cancellationToken: ct)
                .ConfigureAwait(false);
            if (response.Result is null) return fallback;
            return response.Result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Importance judge failed; defaulting to save.");
            return fallback;
        }
    }
}
