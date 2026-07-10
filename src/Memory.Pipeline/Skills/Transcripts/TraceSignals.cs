using System.Text;

namespace Memory.Pipeline.Skills.Transcripts;

/// <summary>
/// Pure heuristics over a <see cref="SessionTrace"/>: decide (for free, no LLM)
/// whether a session plausibly contains skill-worthy experience, and render the
/// trace into a compact prompt block for the Reflector.
/// </summary>
public static class TraceSignals
{
    /// <summary>Phrases (PL/EN) that indicate a user correction — the strongest learning signal (Devin pattern).</summary>
    private static readonly string[] _correctionPhrases =
    [
        "nie rób", "nie tak", "mówiłem", "mówiłam", "wkurza", "zawsze rób", "nigdy nie",
        "przecież", "znowu", "jeszcze raz ci", "popraw to",
        "don't do", "do not do", "i told you", "again!", "stop doing", "you keep", "wrong again",
    ];

    /// <summary>Phrases that explicitly ask for a skill/memory to be created.</summary>
    private static readonly string[] _explicitSkillPhrases =
    [
        "zapisz jako skill", "zrób z tego skill", "zapamiętaj jak", "save that as a skill",
        "make this a skill", "remember how to",
    ];

    public static TraceSignalScore Score(SessionTrace trace)
    {
        var reasons = new List<string>();
        double score = 0;

        // Error → later success arc: something was hard and then worked.
        if (trace.Stats.ToolErrors > 0 && trace.Stats.ToolCalls > trace.Stats.ToolErrors)
        {
            var arc = Math.Min(0.35, 0.15 + 0.05 * trace.Stats.ToolErrors);
            score += arc;
            reasons.Add($"error-then-progress arc ({trace.Stats.ToolErrors} tool error(s))");
        }

        // User corrections.
        var corrections = CountPhraseHits(trace, TraceRole.User, _correctionPhrases);
        if (corrections > 0)
        {
            score += Math.Min(0.30, 0.15 * corrections);
            reasons.Add($"{corrections} user-correction phrase(s)");
        }

        // Explicit "make this a skill".
        if (CountPhraseHits(trace, TraceRole.User, _explicitSkillPhrases) > 0)
        {
            score += 0.50;
            reasons.Add("explicit skill request");
        }

        // Substantial work: enough tool activity that a procedure could exist at all.
        if (trace.Stats.ToolCalls >= 10)
        {
            score += 0.15;
            reasons.Add($"substantial tool activity ({trace.Stats.ToolCalls} calls)");
        }
        else if (trace.Stats.ToolCalls >= 4)
        {
            score += 0.05;
        }

        // Trivial sessions can't teach anything.
        if (trace.Stats.ToolCalls < 2 && trace.Stats.UserMessages <= 1)
        {
            score = 0;
            reasons.Clear();
            reasons.Add("trivial session (fewer than 2 tool calls)");
        }

        return new TraceSignalScore(Math.Min(1.0, score), reasons);
    }

    /// <summary>
    /// Renders the trace for the Reflector prompt. When the trace exceeds the char
    /// budget the middle is elided — openings carry the task framing, endings carry
    /// the resolution, and both matter more than the slog in between.
    /// </summary>
    public static string RenderForPrompt(SessionTrace trace, int maxChars = 60_000)
    {
        var blocks = trace.Turns.Select(RenderTurn).ToList();
        var total = blocks.Sum(b => b.Length);
        var sb = new StringBuilder(Math.Min(total, maxChars) + 128);

        if (total <= maxChars)
        {
            foreach (var block in blocks)
            {
                sb.AppendLine(block);
            }
        }
        else
        {
            var headBudget = maxChars * 55 / 100;
            var tailBudget = maxChars - headBudget;

            var used = 0;
            var headCount = 0;
            foreach (var block in blocks)
            {
                if (used + block.Length > headBudget)
                {
                    break;
                }
                sb.AppendLine(block);
                used += block.Length;
                headCount++;
            }

            var tail = new List<string>();
            used = 0;
            for (var i = blocks.Count - 1; i > headCount; i--)
            {
                if (used + blocks[i].Length > tailBudget)
                {
                    break;
                }
                tail.Add(blocks[i]);
                used += blocks[i].Length;
            }

            sb.AppendLine($"[… {blocks.Count - headCount - tail.Count} turn(s) elided …]");
            for (var i = tail.Count - 1; i >= 0; i--)
            {
                sb.AppendLine(tail[i]);
            }
        }

        return sb.ToString();
    }

    private static string RenderTurn(TraceTurn turn)
    {
        var sb = new StringBuilder();
        sb.Append(turn.Role == TraceRole.User ? "USER: " : "ASSISTANT: ");
        if (!string.IsNullOrWhiteSpace(turn.Text))
        {
            sb.Append(turn.Text.ReplaceLineEndings(" ").Trim());
        }

        foreach (var call in turn.ToolCalls)
        {
            sb.AppendLine();
            sb.Append("  TOOL ").Append(call.Tool);
            if (call.IsError)
            {
                sb.Append(" [ERROR]");
            }
            if (call.InputSummary is not null)
            {
                sb.Append(" in: ").Append(call.InputSummary.ReplaceLineEndings(" "));
            }
            if (call.OutputSummary is not null)
            {
                sb.Append(" out: ").Append(call.OutputSummary.ReplaceLineEndings(" "));
            }
        }

        return sb.ToString();
    }

    private static int CountPhraseHits(SessionTrace trace, TraceRole role, string[] phrases)
    {
        var hits = 0;
        foreach (var turn in trace.Turns.Where(t => t.Role == role && t.Text is not null))
        {
            foreach (var phrase in phrases)
            {
                if (turn.Text!.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    hits++;
                }
            }
        }
        return hits;
    }
}

public sealed record TraceSignalScore(double Score, IReadOnlyList<string> Reasons);
