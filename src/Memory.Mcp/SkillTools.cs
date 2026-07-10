using System.ComponentModel;
using Memory.Domain;
using Memory.Pipeline.Skills;
using Memory.Pipeline.Skills.Synthesis;
using Memory.Tenancy;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

[McpServerToolType]
public static class SkillTools
{
    [McpServerTool(Name = "save_skill")]
    [Description("Create or update a reusable skill (Agent Skills / SKILL.md format). " +
                 "Use for hard-won procedural knowledge worth teaching to every future agent session: multi-step workflows, " +
                 "debugging recipes, project-specific procedures. With publish=true the skill goes live immediately " +
                 "(versioned, audited, 1-click rollback); with publish=false it is saved as a draft.")]
    public static async Task<SkillSaveResponse> SaveSkillAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Skill slug: lowercase letters/digits/hyphens, max 64 chars, e.g. 'debugging-age-cypher'.")] string name,
        [Description("Third-person description that drives auto-invocation; state what the skill does AND concrete triggers. Max 1024 chars.")] string description,
        [Description("SKILL.md markdown body: the actual instructions/checklist. Keep under ~500 lines; no secrets.")] string body,
        [Description("Optional extra trigger context (example requests, keywords). Claude-Code frontmatter; folded into description for other agents.")] string? whenToUse = null,
        [Description("Publish immediately (true) or save as draft (false, default).")] bool publish = false,
        [Description("Optional one-line summary of what changed (stored with the version).")] string? changeSummary = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var result = await skills.SaveAsync(new SaveSkillRequest(
            name, description, body, whenToUse,
            Publish: publish,
            CreatedBy: "mcp:save_skill",
            ChangeSummary: changeSummary), ct).ConfigureAwait(false);
        return ToResponse(result);
    }

    [McpServerTool(Name = "propose_skill")]
    [Description("Propose a skill mid-session without writing the full body: records a draft with rationale + evidence and " +
                 "provenance to this conversation. Use when you notice a hard-won discovery, a repeated workflow, or a strong " +
                 "user correction that deserves to become a skill, but fleshing it out now would derail the task.")]
    public static async Task<SkillSaveResponse> ProposeSkillAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Proposed skill name (free-form is OK — it is slugified).")] string name,
        [Description("Third-person description with triggers, max 1024 chars.")] string description,
        [Description("Why this deserves to be a skill: what was discovered, what it saves next time.")] string rationale,
        [Description("Optional verbatim evidence: the commands/fix/quote that worked. No secrets.")] string? evidence = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var result = await skills.ProposeAsync(new ProposeSkillRequest(
            name, description, rationale, evidence,
            CreatedBy: "mcp:propose_skill"), ct).ConfigureAwait(false);
        return ToResponse(result);
    }

    [McpServerTool(Name = "search_skills")]
    [Description("Hybrid search (embedding + text) over the skill library. Use before starting unfamiliar multi-step work " +
                 "to check whether a learned skill already covers it.")]
    public static async Task<SearchSkillsResponse> SearchSkillsAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("What you are about to do, e.g. 'deploy blazor app to homelab'.")] string query,
        [Description("Maximum results (default 10).")] int maxResults = 10,
        [Description("Include deprecated skills (default false).")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var hits = await skills.SearchAsync(query, maxResults, includeDeprecated, ct).ConfigureAwait(false);
        return new SearchSkillsResponse(
            hits.Select(h => new SkillSearchHitView(
                h.Skill.Name,
                h.Skill.Description,
                h.Skill.Status.ToString(),
                h.Skill.CurrentVersion,
                Math.Round(h.Score, 4),
                h.FromVector,
                h.FromText)).ToArray());
    }

    [McpServerTool(Name = "get_skill")]
    [Description("Fetch a skill's full SKILL.md content plus lifecycle metadata and usage counters.")]
    public static async Task<GetSkillResponse> GetSkillAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Skill slug.")] string name,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var skill = await skills.GetAsync(name, ct).ConfigureAwait(false);
        if (skill is null)
        {
            return new GetSkillResponse(false, null, null);
        }

        return new GetSkillResponse(
            true,
            ToView(skill),
            SkillMarkdown.Render(skill, SkillRenderFlavor.ClaudeCode));
    }

    [McpServerTool(Name = "list_skills")]
    [Description("List skills in the library with status, version and usage counters. Optionally filter by status " +
                 "(draft/candidate/published/deprecated/rejected).")]
    public static async Task<ListSkillsResponse> ListSkillsAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Optional status filter: draft, candidate, published, deprecated, rejected.")] string? status = null,
        [Description("Maximum rows (default 100).")] int limit = 100,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        SkillStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SkillStatus>(status, ignoreCase: true, out var parsed))
            {
                return new ListSkillsResponse([], $"unknown status '{status}'");
            }
            filter = parsed;
        }

        var list = await skills.ListAsync(filter, limit, ct).ConfigureAwait(false);
        return new ListSkillsResponse(list.Select(ToView).ToArray(), null);
    }

    [McpServerTool(Name = "skill_feedback")]
    [Description("Record that a skill helped or misled you. Call after actually using a skill: outcome=helpful when it saved " +
                 "time or prevented a mistake, outcome=harmful when it was wrong/outdated/misleading. Counters drive ordering " +
                 "and deprecation decisions.")]
    public static async Task<SkillLifecycleResponse> SkillFeedbackAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Skill slug.")] string name,
        [Description("helpful | harmful | unknown (unknown = usage ping without a verdict).")] string outcome,
        [Description("Optional detail: what exactly helped or misled.")] string? detail = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        if (!Enum.TryParse<SkillUsageOutcome>(outcome, ignoreCase: true, out var parsed))
        {
            return new SkillLifecycleResponse(false, name, $"unknown outcome '{outcome}' — use helpful, harmful, or unknown");
        }

        var result = await skills.FeedbackAsync(name, parsed, detail, "mcp", ct).ConfigureAwait(false);
        return new SkillLifecycleResponse(result.Success, result.Name, result.Message);
    }

    [McpServerTool(Name = "promote_skill")]
    [Description("Publish a draft/candidate skill: bumps the version, snapshots it for rollback, and makes it visible to " +
                 "sync and MCP consumers.")]
    public static async Task<SkillLifecycleResponse> PromoteSkillAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Skill slug.")] string name,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var result = await skills.PromoteAsync(name, "mcp:promote_skill", ct).ConfigureAwait(false);
        return new SkillLifecycleResponse(result.Success, result.Name, result.Message);
    }

    [McpServerTool(Name = "deprecate_skill")]
    [Description("Retire a skill: it stops being synced/served but stays in history (never deleted). Use for stale, wrong, " +
                 "or superseded skills.")]
    public static async Task<SkillLifecycleResponse> DeprecateSkillAsync(
        ISkillService skills,
        ITenantContext tenant,
        [Description("Skill slug.")] string name,
        [Description("Short audit reason.")] string reason,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var result = await skills.DeprecateAsync(name, reason, "mcp:deprecate_skill", ct).ConfigureAwait(false);
        return new SkillLifecycleResponse(result.Success, result.Name, result.Message);
    }

    [McpServerTool(Name = "synthesize_skills")]
    [Description("Process pending harvested session transcripts into skill candidates: heuristic prefilter → LLM reflection " +
                 "(typed deltas vs the existing library) → dedup → draft → quality/security gates. In Suggest mode results land " +
                 "as Candidates for review (promote_skill); in AutoExecute they publish directly. Costs a few LLM calls per " +
                 "eligible session — sessions below the signal threshold are skipped for free.")]
    public static async Task<SynthesizeSkillsResponse> SynthesizeSkillsAsync(
        ISkillSynthesizer synthesizer,
        ITenantContext tenant,
        [Description("Maximum pending sessions to process this run (default from config, clamp 1-20).")] int? maxSessions = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var result = await synthesizer.RunOnceAsync(maxSessions, ct).ConfigureAwait(false);
        return new SynthesizeSkillsResponse(
            result.SessionsClaimed,
            result.SessionsProcessed,
            result.SessionsSkipped,
            result.SessionsFailed,
            result.SkillsCreated.ToArray(),
            result.SkillsUpdated.ToArray(),
            result.Messages.ToArray());
    }

    private static SkillSaveResponse ToResponse(SkillSaveResult result) =>
        new(result.Success, result.Name, result.SkillId, result.Status?.ToString(), result.Version, result.Message);

    private static SkillView ToView(Skill s) => new(
        s.Name,
        s.Description,
        s.WhenToUse,
        s.Status.ToString(),
        s.Origin.ToString(),
        s.CurrentVersion,
        s.HelpfulCount,
        s.HarmfulCount,
        s.UsageCount,
        s.LastUsedAt,
        s.UpdatedAt,
        s.UntrustedInput);
}

public sealed record SkillSaveResponse(
    bool Success,
    string Name,
    string? SkillId,
    string? Status,
    int Version,
    string Message);

public sealed record SkillLifecycleResponse(
    bool Success,
    string Name,
    string Message);

public sealed record SkillView(
    string Name,
    string Description,
    string? WhenToUse,
    string Status,
    string Origin,
    int Version,
    int HelpfulCount,
    int HarmfulCount,
    int UsageCount,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset UpdatedAt,
    bool UntrustedInput);

public sealed record SkillSearchHitView(
    string Name,
    string Description,
    string Status,
    int Version,
    double Score,
    bool FromVector,
    bool FromText);

public sealed record SearchSkillsResponse(
    SkillSearchHitView[] Hits);

public sealed record GetSkillResponse(
    bool Found,
    SkillView? Skill,
    string? SkillMarkdownContent);

public sealed record ListSkillsResponse(
    SkillView[] Skills,
    string? Error);

public sealed record SynthesizeSkillsResponse(
    int SessionsClaimed,
    int SessionsProcessed,
    int SessionsSkipped,
    int SessionsFailed,
    string[] SkillsCreated,
    string[] SkillsUpdated,
    string[] Messages);
