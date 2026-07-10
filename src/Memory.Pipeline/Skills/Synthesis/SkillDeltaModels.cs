namespace Memory.Pipeline.Skills.Synthesis;

/// <summary>Reflector output: typed deltas against the existing skill library (ACE-style — deltas only, never rewrites).</summary>
public sealed record SkillDeltaBatch(List<SkillDelta> Deltas, string? SessionSummary);

/// <param name="Op">create | update | upvote | downvote</param>
/// <param name="Evidence">Verbatim quotes / commands from the session proving the knowledge worked.</param>
public sealed record SkillDelta(
    string Op,
    string? TargetSkillName,
    string? Name,
    string? Description,
    string? WhenToUse,
    string? BodyOutline,
    List<string>? Evidence,
    double Confidence);

/// <summary>Drafter output: a complete SKILL.md-shaped draft.</summary>
public sealed record SkillDraft(
    string Name,
    string Description,
    string? WhenToUse,
    string Body,
    string ChangeSummary);

/// <summary>Curator LLM verdict when a new delta lands near an existing skill.</summary>
/// <param name="Op">add | update | noop</param>
public sealed record CuratorVerdict(string Op, string? TargetSkillName, string Reason);

/// <summary>Quality-gate verdict.</summary>
public sealed record QualityVerdict(double Score, string Reason);

public sealed record SynthesisRunResult(
    int SessionsClaimed,
    int SessionsProcessed,
    int SessionsSkipped,
    int SessionsFailed,
    IReadOnlyList<string> SkillsCreated,
    IReadOnlyList<string> SkillsUpdated,
    IReadOnlyList<string> Messages);
