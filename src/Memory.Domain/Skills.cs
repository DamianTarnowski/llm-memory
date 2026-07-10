namespace Memory.Domain;

/// <summary>
/// A reusable procedural skill in the Agent Skills (SKILL.md) format, stored as the
/// single source of truth and rendered to agent-readable files / MCP resources.
/// Skills are versioned on publish and deprecated rather than deleted.
/// </summary>
public sealed class Skill
{
    public required SkillId Id { get; init; }
    public required ProjectId Project { get; init; }

    /// <summary>Spec-compliant slug: lowercase letters/digits/hyphens, max 64 chars, matches the skill directory name.</summary>
    public required string Name { get; init; }

    /// <summary>Drives auto-invocation in agents; third person, includes concrete triggers. Max 1024 chars per the open spec.</summary>
    public required string Description { get; init; }

    /// <summary>Extra trigger context. Claude-Code-only frontmatter; folded into the description for the .agents render.</summary>
    public string? WhenToUse { get; init; }

    /// <summary>SKILL.md markdown body (by convention under 500 lines).</summary>
    public required string Body { get; init; }

    /// <summary>Raw JSON with whitelisted frontmatter extras (argument-hint, paths, metadata) and synthesis extras (trigger probes).</summary>
    public string? FrontmatterExtraJson { get; init; }

    public SkillStatus Status { get; init; } = SkillStatus.Draft;
    public SkillOrigin Origin { get; init; } = SkillOrigin.Manual;

    /// <summary>Bumped on every publish; 0 until first published.</summary>
    public int CurrentVersion { get; init; }

    public int HelpfulCount { get; init; }
    public int HarmfulCount { get; init; }
    public int UsageCount { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeprecatedAt { get; init; }

    /// <summary>Model that drafted the current content, when synthesized.</summary>
    public string? GeneratorModel { get; init; }

    /// <summary>Provenance includes untrusted web/tool content — forces review when the security policy says so.</summary>
    public bool UntrustedInput { get; init; }
}

public enum SkillStatus
{
    /// <summary>Work in progress (agent proposal or partial synthesis output); never rendered/synced.</summary>
    Draft = 0,
    /// <summary>Synthesized and gate-passed, waiting for review (Suggest mode).</summary>
    Candidate = 1,
    /// <summary>Live: rendered by sync and served over MCP.</summary>
    Published = 2,
    /// <summary>Retired but kept for history; excluded from sync and default retrieval.</summary>
    Deprecated = 3,
    /// <summary>Failed a quality/security gate or was rejected in review.</summary>
    Rejected = 4,
}

public enum SkillOrigin
{
    Manual = 0,
    Synthesized = 1,
    Imported = 2,
}

/// <summary>Immutable snapshot of a skill at a publish point; enables 1-click rollback.</summary>
public sealed class SkillVersion
{
    public required SkillId SkillId { get; init; }
    public required int Version { get; init; }
    public required ProjectId Project { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? WhenToUse { get; init; }
    public required string Body { get; init; }
    public string? FrontmatterExtraJson { get; init; }
    public required string ChangeSummary { get; init; }

    /// <summary>Who produced this version: "synthesis", "mcp:save_skill", "web:&lt;user&gt;", "cli".</summary>
    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Back-link from a skill version to the experience that taught it (episode, note, harvested session).</summary>
public sealed class SkillProvenance
{
    public required Guid Id { get; init; }
    public required SkillId SkillId { get; init; }
    public required int Version { get; init; }
    public required ProjectId Project { get; init; }

    /// <summary>"episode" | "note" | "harvested_session" | "conversation".</summary>
    public required string SourceKind { get; init; }

    /// <summary>Uuid or free-form reference into the source kind.</summary>
    public required string SourceRef { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Embedding of name + description + when_to_use — what dedup and retrieval match against.</summary>
public sealed class SkillEmbedding
{
    public required SkillId SkillId { get; init; }
    public required ProjectId Project { get; init; }
    public required string EmbeddingModel { get; init; }
    public required int Dimensions { get; init; }
    public required float[] Embedding { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class SkillUsageEvent
{
    public required Guid Id { get; init; }
    public required SkillId SkillId { get; init; }
    public required ProjectId Project { get; init; }

    /// <summary>Client that produced the signal: "claude-code", "codex", "mcp", "web", "cli".</summary>
    public required string Source { get; init; }

    public string? SessionRef { get; init; }
    public SkillUsageOutcome Outcome { get; init; } = SkillUsageOutcome.Unknown;
    public string? Detail { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public enum SkillUsageOutcome
{
    Unknown = 0,
    Helpful = 1,
    Harmful = 2,
}

/// <summary>
/// Synthesis inbox row: a captured agent session transcript waiting to be distilled.
/// Idempotent on (project, content hash); workers claim rows with FOR UPDATE SKIP LOCKED.
/// </summary>
public sealed class HarvestedSession
{
    public required Guid Id { get; init; }
    public required ProjectId Project { get; init; }

    /// <summary>"claude-code" | "codex".</summary>
    public required string Source { get; init; }

    /// <summary>Client-side session id (from the transcript filename / hook payload).</summary>
    public required string SessionId { get; init; }

    /// <summary>SHA-256 hex of the raw transcript — dedup key so PreCompact + SessionEnd double-fires are safe.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Gzip-compressed transcript body; null when only a path reference is stored (local same-filesystem case).</summary>
    public byte[]? TranscriptGzip { get; init; }

    /// <summary>Path reference used instead of the body when CLI and server share a filesystem view.</summary>
    public string? TranscriptPath { get; init; }

    public HarvestStatus Status { get; init; } = HarvestStatus.Pending;

    /// <summary>Raw JSON stats: turns, tool calls, errors, duration, cwd, git branch.</summary>
    public string? StatsJson { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public string? Error { get; init; }
}

public enum HarvestStatus
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    Skipped = 3,
    Failed = 4,
}
