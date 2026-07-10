using Memory.Domain;

namespace Memory.Pipeline.Skills;

/// <summary>
/// Application service for the skill library: CRUD with versioning, lifecycle
/// transitions with audit episodes, feedback counters, and hybrid retrieval.
/// Tenancy is enforced at the tool/endpoint boundary plus RLS beneath — every
/// method assumes an established tenant scope.
/// </summary>
public interface ISkillService
{
    Task<SkillSaveResult> SaveAsync(SaveSkillRequest request, CancellationToken ct = default);

    Task<SkillSaveResult> ProposeAsync(ProposeSkillRequest request, CancellationToken ct = default);

    Task<Skill?> GetAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<Skill>> ListAsync(SkillStatus? status = null, int limit = 100, CancellationToken ct = default);

    /// <summary>Published skills only — the set rendered by sync and served over MCP.</summary>
    Task<IReadOnlyList<Skill>> ListPublishedAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SkillSearchHit>> SearchAsync(
        string query, int maxResults = 10, bool includeDeprecated = false, CancellationToken ct = default);

    Task<SkillLifecycleResult> PromoteAsync(string name, string actor, CancellationToken ct = default);

    Task<SkillLifecycleResult> DeprecateAsync(string name, string reason, string actor, CancellationToken ct = default);

    Task<SkillLifecycleResult> FeedbackAsync(
        string name, SkillUsageOutcome outcome, string? detail, string source, CancellationToken ct = default);
}

/// <param name="Publish">
/// True publishes directly (bump version + snapshot). Explicit saves are never
/// funneled into a review queue — AutomationMode gates synthesis output only.
/// </param>
public sealed record SaveSkillRequest(
    string Name,
    string Description,
    string Body,
    string? WhenToUse = null,
    string? FrontmatterExtraJson = null,
    bool Publish = false,
    string CreatedBy = "unknown",
    SkillOrigin Origin = SkillOrigin.Manual,
    string? GeneratorModel = null,
    bool UntrustedInput = false,
    string? ChangeSummary = null,
    IReadOnlyList<SkillProvenanceRef>? Provenance = null,
    bool AsCandidate = false);

public sealed record ProposeSkillRequest(
    string Name,
    string Description,
    string Rationale,
    string? Evidence = null,
    string CreatedBy = "unknown",
    IReadOnlyList<SkillProvenanceRef>? Provenance = null);

public sealed record SkillProvenanceRef(string SourceKind, string SourceRef);

public sealed record SkillSaveResult(
    bool Success,
    string Name,
    string? SkillId,
    SkillStatus? Status,
    int Version,
    string Message);

public sealed record SkillLifecycleResult(bool Success, string Name, string Message);

public sealed record SkillSearchHit(Skill Skill, double Score, bool FromVector, bool FromText);
