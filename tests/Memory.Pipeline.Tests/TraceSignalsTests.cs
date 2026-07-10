using Memory.Pipeline.Skills.Transcripts;

namespace Memory.Pipeline.Tests;

public class TraceSignalsTests
{
    private static TraceTurn User(string text) => new(TraceRole.User, text, [], null);

    private static TraceTurn Assistant(params TraceToolCall[] calls) =>
        new(TraceRole.Assistant, null, calls, null);

    private static SessionTrace Trace(int toolCalls, int toolErrors, params TraceTurn[] turns) =>
        new("claude-code", "t", turns,
            new TraceStats(turns.Length, turns.Count(t => t.Role == TraceRole.User),
                turns.Count(t => t.Role == TraceRole.Assistant), toolCalls, toolErrors, 0, null, null));

    [Fact]
    public void Score_TrivialSession_IsZero()
    {
        var score = TraceSignals.Score(Trace(1, 0, User("hello")));
        Assert.Equal(0, score.Score);
    }

    [Fact]
    public void Score_ErrorThenProgress_Raises()
    {
        var score = TraceSignals.Score(Trace(8, 2, User("fix the build"), Assistant()));
        Assert.True(score.Score > 0.2);
        Assert.Contains(score.Reasons, r => r.Contains("error-then-progress", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nie rób tak, mówiłem już")]
    [InlineData("I told you to use the shared server")]
    public void Score_UserCorrections_Raise(string correction)
    {
        var score = TraceSignals.Score(Trace(4, 0, User(correction)));
        Assert.Contains(score.Reasons, r => r.Contains("correction", StringComparison.Ordinal));
    }

    [Fact]
    public void Score_ExplicitSkillRequest_DominatesScore()
    {
        var score = TraceSignals.Score(Trace(4, 0, User("Great, save that as a skill please")));
        Assert.True(score.Score >= 0.5);
        Assert.Contains(score.Reasons, r => r.Contains("explicit skill request", StringComparison.Ordinal));
    }

    [Fact]
    public void Score_IsCappedAtOne()
    {
        var score = TraceSignals.Score(Trace(20, 5,
            User("nie rób tak, mówiłem już! save that as a skill"), Assistant()));
        Assert.True(score.Score <= 1.0);
    }

    [Fact]
    public void RenderForPrompt_SmallTrace_ContainsEverything()
    {
        var trace = Trace(1, 1,
            User("fix cypher"),
            Assistant(new TraceToolCall("Bash", "psql …", "ERROR: access to library", true)));
        var rendered = TraceSignals.RenderForPrompt(trace);

        Assert.Contains("USER: fix cypher", rendered, StringComparison.Ordinal);
        Assert.Contains("TOOL Bash [ERROR]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderForPrompt_LargeTrace_ElidesMiddle_KeepsHeadAndTail()
    {
        var turns = new List<TraceTurn> { User("START marker task description") };
        for (var i = 0; i < 300; i++)
        {
            turns.Add(User($"middle filler {i} " + new string('x', 400)));
        }
        turns.Add(User("END marker resolution"));

        var rendered = TraceSignals.RenderForPrompt(
            Trace(10, 0, turns.ToArray()), maxChars: 20_000);

        Assert.True(rendered.Length < 25_000);
        Assert.Contains("START marker", rendered, StringComparison.Ordinal);
        Assert.Contains("END marker", rendered, StringComparison.Ordinal);
        Assert.Contains("elided", rendered, StringComparison.Ordinal);
    }
}
