using System.Data;
using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.Pipeline.Skills;

internal sealed class SkillService(
    ITenantContext tenant,
    MemoryDbContext db,
    ILlmGateway llm,
    IOptions<LlmOptions> llmOptions,
    TimeProvider time,
    ILogger<SkillService> logger) : ISkillService
{
    private const int MaxBodyChars = 64 * 1024;

    public async Task<SkillSaveResult> SaveAsync(SaveSkillRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var name = request.Name?.Trim() ?? string.Empty;

        if (!SkillMarkdown.IsValidName(name))
        {
            return Invalid(name, $"invalid skill name — use a lowercase slug (letters/digits/hyphens, max {SkillMarkdown.MaxNameLength} chars)");
        }
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > SkillMarkdown.MaxDescriptionLength)
        {
            return Invalid(name, $"description is required and must be at most {SkillMarkdown.MaxDescriptionLength} chars");
        }
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Invalid(name, "body is required");
        }
        if (request.Body.Length > MaxBodyChars)
        {
            return Invalid(name, $"body exceeds {MaxBodyChars} chars — split the skill or move detail elsewhere");
        }

        var now = time.GetUtcNow();
        var body = SkillMarkdown.SanitizeBody(request.Body);
        var existing = await db.Skills.FirstOrDefaultAsync(s => s.Name == name, ct).ConfigureAwait(false);

        SkillId skillId;
        int version;
        SkillStatus status;

        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            if (existing is null)
            {
                skillId = SkillId.New();
                version = request.Publish ? 1 : 0;
                status = request.Publish
                    ? SkillStatus.Published
                    : request.AsCandidate ? SkillStatus.Candidate : SkillStatus.Draft;

                db.Skills.Add(new Skill
                {
                    Id = skillId,
                    Project = scope.Project,
                    Name = name,
                    Description = request.Description.Trim(),
                    WhenToUse = NullIfBlank(request.WhenToUse),
                    Body = body,
                    FrontmatterExtraJson = NullIfBlank(request.FrontmatterExtraJson),
                    Status = status,
                    Origin = request.Origin,
                    CurrentVersion = version,
                    CreatedAt = now,
                    UpdatedAt = now,
                    GeneratorModel = NullIfBlank(request.GeneratorModel),
                    UntrustedInput = request.UntrustedInput,
                });
            }
            else
            {
                skillId = existing.Id;
                version = request.Publish ? existing.CurrentVersion + 1 : existing.CurrentVersion;
                status = request.Publish
                    ? SkillStatus.Published
                    : request.AsCandidate && existing.Status is SkillStatus.Draft ? SkillStatus.Candidate : existing.Status;
                var untrusted = existing.UntrustedInput || request.UntrustedInput;
                var generatorModel = NullIfBlank(request.GeneratorModel) ?? existing.GeneratorModel;
                var whenToUse = NullIfBlank(request.WhenToUse);
                var extra = NullIfBlank(request.FrontmatterExtraJson);
                var description = request.Description.Trim();

                await db.Skills.Where(s => s.Id == skillId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Description, description)
                        .SetProperty(x => x.WhenToUse, whenToUse)
                        .SetProperty(x => x.Body, body)
                        .SetProperty(x => x.FrontmatterExtraJson, extra)
                        .SetProperty(x => x.Status, status)
                        .SetProperty(x => x.CurrentVersion, version)
                        .SetProperty(x => x.UpdatedAt, now)
                        .SetProperty(x => x.DeprecatedAt, request.Publish ? null : existing.DeprecatedAt)
                        .SetProperty(x => x.GeneratorModel, generatorModel)
                        .SetProperty(x => x.UntrustedInput, untrusted), ct)
                    .ConfigureAwait(false);
            }

            if (request.Publish)
            {
                db.SkillVersions.Add(new SkillVersion
                {
                    SkillId = skillId,
                    Version = version,
                    Project = scope.Project,
                    Name = name,
                    Description = request.Description.Trim(),
                    WhenToUse = NullIfBlank(request.WhenToUse),
                    Body = body,
                    FrontmatterExtraJson = NullIfBlank(request.FrontmatterExtraJson),
                    ChangeSummary = NullIfBlank(request.ChangeSummary) ?? (existing is null ? "initial publish" : "updated"),
                    CreatedBy = request.CreatedBy,
                    CreatedAt = now,
                });
            }

            AddProvenance(request.Provenance, skillId, version, scope.Project, now);
            WriteAuditEpisode(scope.Project,
                request.Publish ? "publish_skill" : "save_skill",
                $"{name} v{version} by {request.CreatedBy}", now, name);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await UpsertEmbeddingAsync(skillId, scope.Project, name, request.Description, request.WhenToUse, now, ct)
            .ConfigureAwait(false);

        return new SkillSaveResult(true, name, skillId.ToString(), status, version,
            request.Publish ? "published" : (existing is null ? "saved as draft" : "updated"));
    }

    public async Task<SkillSaveResult> ProposeAsync(ProposeSkillRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var name = SkillMarkdown.IsValidName(request.Name?.Trim())
            ? request.Name!.Trim()
            : SkillMarkdown.Slugify(request.Name);

        if (name is null)
        {
            return Invalid(request.Name ?? "", "could not derive a valid slug from the proposed name");
        }

        var existing = await db.Skills.FirstOrDefaultAsync(s => s.Name == name, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();

        if (existing is not null)
        {
            // Don't overwrite — record the new evidence against the existing skill instead.
            AddProvenance(request.Provenance, existing.Id, existing.CurrentVersion, scope.Project, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new SkillSaveResult(true, name, existing.Id.ToString(), existing.Status, existing.CurrentVersion,
                "skill already exists — proposal recorded as additional provenance");
        }

        var body = BuildProposalBody(request);
        return await SaveAsync(new SaveSkillRequest(
            name,
            request.Description,
            body,
            Publish: false,
            CreatedBy: request.CreatedBy,
            Provenance: request.Provenance), ct).ConfigureAwait(false);
    }

    public Task<Skill?> GetAsync(string name, CancellationToken ct = default)
    {
        _ = tenant.Require();
        var normalized = name.Trim();
        return db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Name == normalized, ct);
    }

    public async Task<IReadOnlyList<Skill>> ListAsync(SkillStatus? status = null, int limit = 100, CancellationToken ct = default)
    {
        _ = tenant.Require();
        var clamped = Math.Clamp(limit, 1, 500);
        var query = db.Skills.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            query = query.Where(x => x.Status == s);
        }

        return await query
            .OrderByDescending(x => x.Status == SkillStatus.Published)
            .ThenBy(x => x.Name)
            .Take(clamped)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Skill>> ListPublishedAsync(CancellationToken ct = default)
    {
        _ = tenant.Require();
        return await db.Skills.AsNoTracking()
            .Where(s => s.Status == SkillStatus.Published)
            .OrderBy(s => s.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillSearchHit>> SearchAsync(
        string query, int maxResults = 10, bool includeDeprecated = false, CancellationToken ct = default)
    {
        _ = tenant.Require();
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var limit = Math.Clamp(maxResults, 1, 50);
        var hits = new Dictionary<SkillId, SkillSearchHit>();

        // Vector stream — best effort; embedding provider being down degrades to text match.
        try
        {
            var embeddings = await llm.GetEmbeddings()
                .GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false);
            var queryVector = new Pgvector.Vector(embeddings[0].Vector.ToArray());

            if (db.Database.GetDbConnection().State != ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            }
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();

            await using var cmd = new NpgsqlCommand
            {
                Connection = conn,
                CommandText = $"""
                    SELECT s.id, se.embedding <=> @query AS distance
                    FROM memory.skills s
                    JOIN memory.skill_embeddings se ON se.skill_id = s.id
                    WHERE s.status <> {(short)SkillStatus.Rejected}
                    {(includeDeprecated ? "" : $"AND s.status <> {(short)SkillStatus.Deprecated}")}
                    ORDER BY distance ASC
                    LIMIT @limit
                    """,
            };
            cmd.Parameters.AddWithValue("query", queryVector);
            cmd.Parameters.AddWithValue("limit", limit);

            var ranked = new List<(SkillId Id, double Distance)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    ranked.Add((new SkillId(reader.GetGuid(0)), reader.GetDouble(1)));
                }
            }

            if (ranked.Count > 0)
            {
                var ids = ranked.Select(r => r.Id).ToList();
                var skills = await db.Skills.AsNoTracking()
                    .Where(s => ids.Contains(s.Id))
                    .ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
                foreach (var (id, distance) in ranked)
                {
                    if (skills.TryGetValue(id, out var skill))
                    {
                        hits[id] = new SkillSearchHit(skill, Math.Max(0, 1 - distance), FromVector: true, FromText: false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skill vector search failed; falling back to text match.");
        }

        // Text stream — always runs; fills gaps left by the vector stream.
        var pattern = $"%{query.Trim()}%";
        var textMatches = await db.Skills.AsNoTracking()
            .Where(s => s.Status != SkillStatus.Rejected)
            .Where(s => includeDeprecated || s.Status != SkillStatus.Deprecated)
            .Where(s => EF.Functions.ILike(s.Name, pattern)
                || EF.Functions.ILike(s.Description, pattern)
                || EF.Functions.ILike(s.Body, pattern))
            .OrderBy(s => s.Name)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var skill in textMatches)
        {
            if (hits.TryGetValue(skill.Id, out var vectorHit))
            {
                hits[skill.Id] = vectorHit with { FromText = true };
            }
            else if (hits.Count < limit)
            {
                hits[skill.Id] = new SkillSearchHit(skill, 0.30, FromVector: false, FromText: true);
            }
        }

        return hits.Values.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    public async Task<SkillLifecycleResult> PromoteAsync(string name, string actor, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name.Trim(), ct).ConfigureAwait(false);
        if (skill is null)
        {
            return new SkillLifecycleResult(false, name, "skill not found");
        }
        if (skill.Status == SkillStatus.Published)
        {
            return new SkillLifecycleResult(false, name, "already published");
        }

        var result = await SaveAsync(new SaveSkillRequest(
            skill.Name, skill.Description, skill.Body, skill.WhenToUse, skill.FrontmatterExtraJson,
            Publish: true, CreatedBy: actor,
            ChangeSummary: $"promoted from {skill.Status}"), ct).ConfigureAwait(false);

        return new SkillLifecycleResult(result.Success, name,
            result.Success ? $"published as v{result.Version}" : result.Message);
    }

    public async Task<SkillLifecycleResult> DeprecateAsync(string name, string reason, string actor, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var now = time.GetUtcNow();
        var normalized = name.Trim();

        var updated = await db.Skills
            .Where(s => s.Name == normalized && s.Status != SkillStatus.Deprecated)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, SkillStatus.Deprecated)
                .SetProperty(x => x.DeprecatedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            return new SkillLifecycleResult(false, name, "skill not found or already deprecated");
        }

        WriteAuditEpisode(scope.Project, "deprecate_skill", $"{normalized}: {reason} (by {actor})", now, normalized);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new SkillLifecycleResult(true, name, "deprecated");
    }

    public async Task<SkillLifecycleResult> FeedbackAsync(
        string name, SkillUsageOutcome outcome, string? detail, string source, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var normalized = name.Trim();
        var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Name == normalized, ct).ConfigureAwait(false);
        if (skill is null)
        {
            return new SkillLifecycleResult(false, name, "skill not found");
        }

        var now = time.GetUtcNow();
        await db.Skills.Where(s => s.Id == skill.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.UsageCount, x => x.UsageCount + 1)
                .SetProperty(x => x.HelpfulCount, x => outcome == SkillUsageOutcome.Helpful ? x.HelpfulCount + 1 : x.HelpfulCount)
                .SetProperty(x => x.HarmfulCount, x => outcome == SkillUsageOutcome.Harmful ? x.HarmfulCount + 1 : x.HarmfulCount)
                .SetProperty(x => x.LastUsedAt, now), ct)
            .ConfigureAwait(false);

        db.SkillUsageEvents.Add(new SkillUsageEvent
        {
            Id = Guid.NewGuid(),
            SkillId = skill.Id,
            Project = scope.Project,
            Source = source,
            Outcome = outcome,
            Detail = NullIfBlank(detail),
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new SkillLifecycleResult(true, name, $"recorded {outcome}");
    }

    private async Task UpsertEmbeddingAsync(
        SkillId skillId, ProjectId project, string name, string description, string? whenToUse,
        DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(whenToUse)
                ? $"{name}\n{description}"
                : $"{name}\n{description}\n{whenToUse}";
            var embeddings = await llm.GetEmbeddings()
                .GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
            var vector = embeddings[0].Vector.ToArray();

            await db.SkillEmbeddings.Where(e => e.SkillId == skillId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.SkillEmbeddings.Add(new SkillEmbedding
            {
                SkillId = skillId,
                Project = project,
                EmbeddingModel = llmOptions.Value.EmbeddingModel,
                Dimensions = vector.Length,
                Embedding = vector,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a skill without an embedding is still fully usable
            // (text search + sync + MCP); the next save retries.
            logger.LogWarning(ex, "Skill embedding upsert failed for {Skill}.", name);
        }
    }

    private void AddProvenance(
        IReadOnlyList<SkillProvenanceRef>? provenance, SkillId skillId, int version,
        ProjectId project, DateTimeOffset now)
    {
        if (provenance is not { Count: > 0 })
        {
            return;
        }

        foreach (var p in provenance)
        {
            if (string.IsNullOrWhiteSpace(p.SourceKind) || string.IsNullOrWhiteSpace(p.SourceRef))
            {
                continue;
            }

            db.SkillProvenances.Add(new SkillProvenance
            {
                Id = Guid.NewGuid(),
                SkillId = skillId,
                Version = version,
                Project = project,
                SourceKind = p.SourceKind.Trim(),
                SourceRef = p.SourceRef.Trim(),
                CreatedAt = now,
            });
        }
    }

    private void WriteAuditEpisode(ProjectId project, string action, string detail, DateTimeOffset now, string skillName)
    {
        db.Episodes.Add(new Episode
        {
            Id = EpisodeId.New(),
            Project = project,
            Source = "skill_lifecycle",
            Content = $"{action}: {detail}",
            OccurredAt = now,
            IngestedAt = now,
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "skill_lifecycle",
                ["action"] = action,
                ["skill"] = skillName,
            },
        });
    }

    private static string BuildProposalBody(ProposeSkillRequest request)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Proposal");
        sb.AppendLine();
        sb.AppendLine(request.Rationale.Trim());
        if (!string.IsNullOrWhiteSpace(request.Evidence))
        {
            sb.AppendLine();
            sb.AppendLine("## Evidence");
            sb.AppendLine();
            sb.AppendLine(request.Evidence.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("_Draft proposed mid-session; mature into a full skill body before publishing._");
        return sb.ToString();
    }

    private static SkillSaveResult Invalid(string name, string message) =>
        new(false, name, null, null, 0, message);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
