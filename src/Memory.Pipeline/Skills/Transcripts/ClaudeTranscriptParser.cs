using System.Text.Json;

namespace Memory.Pipeline.Skills.Transcripts;

/// <summary>
/// Defensive parser for Claude Code session transcripts
/// (<c>~/.claude/projects/&lt;encoded-path&gt;/&lt;session-id&gt;.jsonl</c>).
///
/// The format is append-only JSONL and evolves with client versions, so the
/// parser is deliberately tolerant: unknown line types, meta lines
/// (<c>mode</c>, <c>ai-title</c>, <c>file-history-snapshot</c>, …), sidechain
/// (subagent) entries, and unparseable lines are counted and skipped — never
/// fatal. Only main-thread user/assistant messages become turns. Thinking
/// blocks are dropped; tool results are joined to their <c>tool_use</c> call
/// by id across lines.
/// </summary>
public static class ClaudeTranscriptParser
{
    private const int MaxInputSummaryChars = 240;
    private const int MaxOutputSummaryChars = 400;
    private const int MaxTextChars = 8_000;

    public static SessionTrace Parse(string transcriptJsonl, string sessionId = "unknown")
    {
        var turns = new List<TraceTurn>();
        var callsByToolUseId = new Dictionary<string, TraceToolCall>(StringComparer.Ordinal);
        var callOrder = new List<(string Id, int TurnIndex, int CallIndex)>();

        var userMessages = 0;
        var assistantMessages = 0;
        var toolErrors = 0;
        var skipped = 0;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;

        foreach (var line in transcriptJsonl.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                var type = GetString(root, "type");
                if (type is not ("user" or "assistant"))
                {
                    skipped++;
                    continue;
                }
                if (GetBool(root, "isSidechain") || GetBool(root, "isMeta"))
                {
                    skipped++;
                    continue;
                }
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                var timestamp = GetTimestamp(root);
                if (timestamp is { } ts)
                {
                    startedAt ??= ts;
                    endedAt = ts;
                }

                if (type == "user")
                {
                    var (text, results) = ParseUserMessage(message);

                    // Attach tool results to their originating calls (they arrive
                    // in later lines than the assistant's tool_use).
                    foreach (var (toolUseId, output, isError) in results)
                    {
                        if (callsByToolUseId.TryGetValue(toolUseId, out var call))
                        {
                            var updated = call with
                            {
                                OutputSummary = output,
                                IsError = call.IsError || isError,
                            };
                            callsByToolUseId[toolUseId] = updated;
                            var pos = callOrder.FirstOrDefault(c => c.Id == toolUseId);
                            if (pos.Id is not null && pos.TurnIndex < turns.Count)
                            {
                                var turn = turns[pos.TurnIndex];
                                var calls = turn.ToolCalls.ToArray();
                                if (pos.CallIndex < calls.Length)
                                {
                                    calls[pos.CallIndex] = updated;
                                    turns[pos.TurnIndex] = turn with { ToolCalls = calls };
                                }
                            }
                        }
                        if (isError)
                        {
                            toolErrors++;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        userMessages++;
                        turns.Add(new TraceTurn(TraceRole.User, Truncate(text, MaxTextChars), [], timestamp));
                    }
                }
                else // assistant
                {
                    var (text, toolCalls) = ParseAssistantMessage(message);
                    if (string.IsNullOrWhiteSpace(text) && toolCalls.Count == 0)
                    {
                        skipped++; // thinking-only line
                        continue;
                    }

                    assistantMessages++;
                    var turnIndex = turns.Count;
                    turns.Add(new TraceTurn(
                        TraceRole.Assistant,
                        string.IsNullOrWhiteSpace(text) ? null : Truncate(text, MaxTextChars),
                        toolCalls.Select(c => c.Call).ToArray(),
                        timestamp));

                    for (var i = 0; i < toolCalls.Count; i++)
                    {
                        if (toolCalls[i].ToolUseId is { } id)
                        {
                            callsByToolUseId[id] = toolCalls[i].Call;
                            callOrder.Add((id, turnIndex, i));
                        }
                    }
                }
            }
        }

        var totalToolCalls = callOrder.Count;
        return new SessionTrace(
            "claude-code",
            sessionId,
            turns,
            new TraceStats(
                turns.Count,
                userMessages,
                assistantMessages,
                totalToolCalls,
                toolErrors,
                skipped,
                startedAt,
                endedAt));
    }

    private static (string? Text, List<(string ToolUseId, string? Output, bool IsError)> Results) ParseUserMessage(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return (null, []);
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return (content.GetString(), []);
        }

        string? text = null;
        var results = new List<(string, string?, bool)>();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (GetString(block, "type"))
                {
                    case "text":
                        text = AppendText(text, GetString(block, "text"));
                        break;

                    case "tool_result":
                        var toolUseId = GetString(block, "tool_use_id");
                        if (toolUseId is not null)
                        {
                            results.Add((
                                toolUseId,
                                Truncate(ExtractBlockContentText(block), MaxOutputSummaryChars),
                                GetBool(block, "is_error")));
                        }
                        break;
                }
            }
        }

        return (text, results);
    }

    private static (string? Text, List<(TraceToolCall Call, string? ToolUseId)> Calls) ParseAssistantMessage(JsonElement message)
    {
        string? text = null;
        var calls = new List<(TraceToolCall, string?)>();

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (GetString(block, "type"))
                {
                    case "text":
                        text = AppendText(text, GetString(block, "text"));
                        break;

                    case "tool_use":
                        var name = GetString(block, "name") ?? "unknown";
                        string? inputSummary = null;
                        if (block.TryGetProperty("input", out var input))
                        {
                            inputSummary = Truncate(input.GetRawText(), MaxInputSummaryChars);
                        }
                        calls.Add((new TraceToolCall(name, inputSummary, null, false), GetString(block, "id")));
                        break;

                        // thinking blocks intentionally dropped
                }
            }
        }

        return (text, calls);
    }

    /// <summary>tool_result content is a string in older clients and a block array in newer ones.</summary>
    private static string? ExtractBlockContentText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            string? text = null;
            foreach (var inner in content.EnumerateArray())
            {
                if (GetString(inner, "type") == "text")
                {
                    text = AppendText(text, GetString(inner, "text"));
                }
            }
            return text;
        }

        return null;
    }

    private static string? AppendText(string? existing, string? addition) =>
        string.IsNullOrWhiteSpace(addition)
            ? existing
            : existing is null ? addition : $"{existing}\n{addition}";

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetTimestamp(JsonElement root) =>
        DateTimeOffset.TryParse(GetString(root, "timestamp"), out var ts) ? ts : null;

    private static string? Truncate(string? value, int maxChars) =>
        value is null || value.Length <= maxChars ? value : value[..maxChars] + "…";
}
