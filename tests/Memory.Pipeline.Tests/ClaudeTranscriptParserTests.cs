using Memory.Pipeline.Skills.Transcripts;

namespace Memory.Pipeline.Tests;

/// <summary>
/// Fixture mirrors the real Claude Code transcript structure 1:1 (field names and
/// block shapes captured from an actual session on 2026-07-10, contents redacted).
/// </summary>
public class ClaudeTranscriptParserTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-transcript-sample.jsonl");

    private static SessionTrace ParseFixture() =>
        ClaudeTranscriptParser.Parse(File.ReadAllText(FixturePath), "fixture-session");

    [Fact]
    public void Parse_ProducesExpectedTurnSequence()
    {
        var trace = ParseFixture();

        // u-1 user, a-2 assistant text, a-3 tool_use, a-4 tool_use, a-5 text, u-5 user
        Assert.Equal(6, trace.Turns.Count);
        Assert.Equal(TraceRole.User, trace.Turns[0].Role);
        Assert.Contains("AGE cypher", trace.Turns[0].Text, StringComparison.Ordinal);
        Assert.Equal(TraceRole.Assistant, trace.Turns[1].Role);
        Assert.Equal(TraceRole.User, trace.Turns[^1].Role);
        Assert.Contains("Save that as a skill", trace.Turns[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_JoinsToolResultsToCalls_AcrossLines()
    {
        var trace = ParseFixture();

        var readCall = trace.Turns.SelectMany(t => t.ToolCalls).Single(c => c.Tool == "Read");
        Assert.False(readCall.IsError);
        Assert.Contains("AgeGraphContext", readCall.OutputSummary, StringComparison.Ordinal);
        Assert.Contains("file_path", readCall.InputSummary, StringComparison.Ordinal);

        var bashCall = trace.Turns.SelectMany(t => t.ToolCalls).Single(c => c.Tool == "Bash");
        Assert.True(bashCall.IsError);
        Assert.Contains("access to library", bashCall.OutputSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Stats_CountsCorrectly()
    {
        var trace = ParseFixture();

        Assert.Equal(2, trace.Stats.UserMessages);        // u-1, u-5 (tool-result-only lines are plumbing)
        Assert.Equal(4, trace.Stats.AssistantMessages);   // a-2, a-3, a-4, a-5 (thinking-only a-1 skipped)
        Assert.Equal(2, trace.Stats.ToolCalls);
        Assert.Equal(1, trace.Stats.ToolErrors);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero), trace.Stats.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 9, 1, 0, TimeSpan.Zero), trace.Stats.EndedAt);
    }

    [Fact]
    public void Parse_SkipsMetaSidechainThinkingAndGarbage()
    {
        var trace = ParseFixture();

        // mode, ai-title, file-history-snapshot, sidechain user, isMeta user,
        // non-JSON line, thinking-only assistant = 7 skipped
        Assert.Equal(7, trace.Stats.SkippedLines);
        Assert.DoesNotContain(trace.Turns, t =>
            t.Text?.Contains("subagent prompt", StringComparison.Ordinal) == true
            || t.Text?.Contains("meta housekeeping", StringComparison.Ordinal) == true
            || t.Text?.Contains("internal reasoning", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyTrace()
    {
        var trace = ClaudeTranscriptParser.Parse("", "s");
        Assert.Empty(trace.Turns);
        Assert.Equal(0, trace.Stats.ToolCalls);
    }

    [Fact]
    public void Parse_RealLocalTranscript_WhenPresent_DoesNotThrow()
    {
        // Opportunistic robustness check against whatever real transcripts exist
        // on the dev machine; contributes nothing on machines without Claude Code.
        var projectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(projectsDir))
        {
            return;
        }

        var transcript = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault(f => new FileInfo(f).Length < 20 * 1024 * 1024);
        if (transcript is null)
        {
            return;
        }

        var trace = ClaudeTranscriptParser.Parse(File.ReadAllText(transcript), Path.GetFileNameWithoutExtension(transcript));
        Assert.True(trace.Turns.Count > 0, "a real transcript should yield at least one turn");
        Assert.True(trace.Stats.ToolCalls >= 0);
    }
}
