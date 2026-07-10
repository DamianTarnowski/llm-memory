namespace Memory.Pipeline.Skills.Transcripts;

/// <summary>
/// Normalized, client-agnostic view of an agent session transcript — the input
/// shape the skill-synthesis Reflector consumes. Produced by per-client parsers
/// (Claude Code today, Codex rollouts later).
/// </summary>
public sealed record SessionTrace(
    string Source,
    string SessionId,
    IReadOnlyList<TraceTurn> Turns,
    TraceStats Stats);

public sealed record TraceTurn(
    TraceRole Role,
    string? Text,
    IReadOnlyList<TraceToolCall> ToolCalls,
    DateTimeOffset? Timestamp);

public enum TraceRole
{
    User = 0,
    Assistant = 1,
}

public sealed record TraceToolCall(
    string Tool,
    string? InputSummary,
    string? OutputSummary,
    bool IsError);

public sealed record TraceStats(
    int Turns,
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int ToolErrors,
    int SkippedLines,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);
