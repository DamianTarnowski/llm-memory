using System.Data;
using Memory.Domain;
using Memory.Llm;
using Memory.Pipeline.Skills.Transcripts;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.Pipeline.Skills.Synthesis;

public interface ISkillSynthesizer
{
    /// <summary>Processes up to <paramref name="maxSessions"/> pending harvested sessions for the current tenant.</summary>
    Task<SynthesisRunResult> RunOnceAsync(int? maxSessions = null, CancellationToken ct = default);
}

/// <summary>
/// The skill-synthesis pipeline: claim pending harvested sessions
/// (<c>FOR UPDATE SKIP LOCKED</c> — safe under concurrent runners), prefilter with
/// free heuristics, then Reflector (typed deltas) → Curator (dedup vs existing
/// library) → Drafter (full SKILL.md) → gates (schema / secrets / injection /
/// quality) → publish decision per <see cref="SkillsOptions.AutomationMode"/>.
/// </summary>
internal sealed class SkillSynthesizer(
    ITenantContext tenant,
    MemoryDbContext db,
    ILlmGateway llm,
    ISkillService skillService,
    IOptions<SkillsOptions> options,
    TimeProvider time,
    ILogger<SkillSynthesizer> logger) : ISkillSynthesizer
{
    private const int MaxDeltasPerSession = 3;
    private const double DedupSimilarityThreshold = 0.85;
    private const double MinQualityScore = 0.5;

    private static readonly string[] _injectionMarkers =
    [
        "ignore previous instructions", "ignore all previous", "disregard the above",
        "disregard all prior", "system prompt", "exfiltrate", "send the contents to http",
    ];

    public async Task<SynthesisRunResult> RunOnceAsync(int? maxSessions = null, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var opts = options.Value;
        var limit = Math.Clamp(maxSessions ?? opts.Synthesis.MaxSessionsPerRun, 1, 20);

        var claimed = await ClaimPendingAsync(limit, ct).ConfigureAwait(false);
        var created = new List<string>();
        var updated = new List<string>();
        var messages = new List<string>();
        var processed = 0; var skipped = 0; var failed = 0;

        foreach (var sessionId in claimed)
        {
            try
            {
                var outcome = await ProcessSessionAsync(sessionId, scope, opts, created, updated, messages, ct)
                    .ConfigureAwait(false);
                if (outcome) { processed++; } else { skipped++; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Skill synthesis failed for harvested session {Id}.", sessionId);
                await MarkAsync(sessionId, HarvestStatus.Failed, ex.Message, ct).ConfigureAwait(false);
            }
        }

        return new SynthesisRunResult(claimed.Count, processed, skipped, failed, created, updated, messages);
    }

    private async Task<bool> ProcessSessionAsync(
        Guid sessionId,
        TenantScope scope,
        SkillsOptions opts,
        List<string> created,
        List<string> updated,
        List<string> messages,
        CancellationToken ct)
    {
        var session = await db.HarvestedSessions.AsNoTracking()
            .FirstAsync(h => h.Id == sessionId, ct).ConfigureAwait(false);

        if (session.Source != "claude-code")
        {
            await MarkAsync(sessionId, HarvestStatus.Skipped, $"no parser for source '{session.Source}' yet", ct).ConfigureAwait(false);
            messages.Add($"{Short(sessionId)}: skipped ({session.Source} parser pending)");
            return false;
        }

        string transcript;
        if (session.TranscriptGzip is { Length: > 0 })
        {
            transcript = HarvestIntake.Decompress(session.TranscriptGzip);
        }
        else if (session.TranscriptPath is not null && File.Exists(session.TranscriptPath))
        {
            transcript = File.ReadAllText(session.TranscriptPath);
        }
        else
        {
            await MarkAsync(sessionId, HarvestStatus.Skipped, "transcript body missing (pruned?)", ct).ConfigureAwait(false);
            messages.Add($"{Short(sessionId)}: skipped (no transcript body)");
            return false;
        }

        var trace = ClaudeTranscriptParser.Parse(transcript, session.SessionId);
        var signal = TraceSignals.Score(trace);
        if (signal.Score < opts.Synthesis.MinSignalScore)
        {
            await MarkAsync(sessionId, HarvestStatus.Skipped,
                $"signal {signal.Score:0.00} < {opts.Synthesis.MinSignalScore:0.00}: {string.Join("; ", signal.Reasons)}", ct)
                .ConfigureAwait(false);
            messages.Add($"{Short(sessionId)}: below signal threshold ({signal.Score:0.00})");
            return false;
        }

        // Sessions that touched the open web taint their skills (prompt-injection channel).
        var untrusted = trace.Turns.SelectMany(t => t.ToolCalls)
            .Any(c => c.Tool.Contains("WebFetch", StringComparison.OrdinalIgnoreCase)
                   || c.Tool.Contains("WebSearch", StringComparison.OrdinalIgnoreCase));

        var digest = await BuildLibraryDigestAsync(ct).ConfigureAwait(false);
        var batch = await ReflectAsync(trace, digest, opts, ct).ConfigureAwait(false);

        var deltaCount = 0;
        foreach (var delta in batch.Deltas.Take(MaxDeltasPerSession))
        {
            ct.ThrowIfCancellationRequested();
            switch (delta.Op?.ToLowerInvariant())
            {
                case "upvote" when delta.TargetSkillName is not null:
                    await skillService.FeedbackAsync(delta.TargetSkillName, SkillUsageOutcome.Helpful,
                        "synthesis: session confirmed this skill", "synthesis", ct).ConfigureAwait(false);
                    deltaCount++;
                    break;

                case "downvote" when delta.TargetSkillName is not null:
                    await skillService.FeedbackAsync(delta.TargetSkillName, SkillUsageOutcome.Harmful,
                        "synthesis: session contradicted this skill", "synthesis", ct).ConfigureAwait(false);
                    deltaCount++;
                    break;

                case "create" or "update":
                    var producedName = await DraftAndPersistAsync(delta, trace, session, untrusted, opts, created, updated, messages, ct)
                        .ConfigureAwait(false);
                    if (producedName is not null)
                    {
                        deltaCount++;
                    }
                    break;
            }
        }

        await MarkAsync(sessionId, HarvestStatus.Processed,
            deltaCount == 0 ? "no skill-worthy deltas" : $"{deltaCount} delta(s) applied", ct).ConfigureAwait(false);
        messages.Add($"{Short(sessionId)}: processed, {deltaCount} delta(s)");
        return true;
    }

    // ------------------------------------------------------------------ reflector

    private const string ReflectorSystemPrompt =
        """
        You distill an agent work session into typed skill deltas for a persistent skill
        library. A skill is a reusable HOW-TO (workflow, debugging recipe, project-specific
        procedure) — not a fact, preference, or one-off detail.

        Extract ONLY knowledge that: (1) required actual discovery in this session
        (failed attempts, non-obvious fix, hard-won procedure), (2) has a clear future
        trigger, and (3) was verified to work in the session. Most sessions contain NO
        skill-worthy knowledge — an empty delta list is the expected common answer.

        Ops:
        - create: genuinely new procedure not covered by the existing library.
        - update: the session refined/extended/corrected an EXISTING skill (set targetSkillName).
        - upvote: the session successfully used an existing skill as-is (set targetSkillName).
        - downvote: the session showed an existing skill is wrong/outdated (set targetSkillName).

        For create/update: name = lowercase-hyphen slug; description = third person with
        concrete triggers; bodyOutline = the steps/pitfalls to include; evidence = short
        verbatim quotes/commands from the session proving it worked. Use placeholders
        instead of machine-specific values. Never include secrets. Confidence 0..1.
        """;

    private async Task<SkillDeltaBatch> ReflectAsync(
        SessionTrace trace, string libraryDigest, SkillsOptions opts, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ReflectorSystemPrompt),
            new(ChatRole.User,
                $"""
                EXISTING SKILL LIBRARY (name: description):
                {libraryDigest}

                SESSION TRANSCRIPT (normalized; tool results truncated):
                {TraceSignals.RenderForPrompt(trace)}
                """),
        };

        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(opts.Synthesis.ReflectorModelOverride))
        {
            chatOptions.ModelId = opts.Synthesis.ReflectorModelOverride;
        }

        var response = await llm.GetChat()
            .GetResponseAsync<SkillDeltaBatch>(messages, options: chatOptions, cancellationToken: ct)
            .ConfigureAwait(false);
        return response.Result ?? new SkillDeltaBatch([], null);
    }

    // ------------------------------------------------------------------ curator + drafter + gates

    private async Task<string?> DraftAndPersistAsync(
        SkillDelta delta,
        SessionTrace trace,
        HarvestedSession session,
        bool untrusted,
        SkillsOptions opts,
        List<string> created,
        List<string> updated,
        List<string> messages,
        CancellationToken ct)
    {
        var isUpdate = string.Equals(delta.Op, "update", StringComparison.OrdinalIgnoreCase);
        var targetName = isUpdate ? delta.TargetSkillName : delta.Name;
        targetName = SkillMarkdown.IsValidName(targetName?.Trim())
            ? targetName!.Trim()
            : SkillMarkdown.Slugify(targetName ?? delta.Name);
        if (targetName is null)
        {
            messages.Add("delta dropped: no usable skill name");
            return null;
        }

        Skill? existing = await skillService.GetAsync(targetName, ct).ConfigureAwait(false);

        // Curator: a "create" landing near an existing skill becomes an update / noop.
        if (!isUpdate && existing is null)
        {
            var near = (await skillService.SearchAsync(
                    $"{delta.Name} {delta.Description}", 3, includeDeprecated: false, ct).ConfigureAwait(false))
                .FirstOrDefault(h => h.FromVector && h.Score >= DedupSimilarityThreshold);
            if (near is not null)
            {
                var verdict = await CurateAsync(delta, near.Skill, ct).ConfigureAwait(false);
                switch (verdict.Op.ToLowerInvariant())
                {
                    case "noop":
                        messages.Add($"delta '{targetName}' curated to noop: {verdict.Reason}");
                        return null;
                    case "update":
                        existing = near.Skill;
                        targetName = near.Skill.Name;
                        isUpdate = true;
                        break;
                }
            }
        }
        else if (isUpdate && existing is null)
        {
            messages.Add($"delta dropped: update target '{targetName}' not found");
            return null;
        }

        var draft = await DraftAsync(delta, trace, existing, ct).ConfigureAwait(false);

        // Gate a: schema.
        var draftName = SkillMarkdown.IsValidName(draft.Name?.Trim()) ? draft.Name!.Trim() : targetName;
        if (string.IsNullOrWhiteSpace(draft.Description) || string.IsNullOrWhiteSpace(draft.Body))
        {
            messages.Add($"draft '{draftName}' rejected: empty description/body");
            return null;
        }
        var description = draft.Description.Length > SkillMarkdown.MaxDescriptionLength
            ? draft.Description[..(SkillMarkdown.MaxDescriptionLength - 1)] + "…"
            : draft.Description;

        // Gate b: secrets — redact rather than reject; redactions are suspicious but salvageable.
        var (body, bodyRedactions) = SecretScanner.Redact(draft.Body);
        var untrustedFinal = untrusted || bodyRedactions > 0;

        // Gate c: injection heuristics — hard reject.
        var lowerBody = body.ToLowerInvariant();
        if (_injectionMarkers.Any(m => lowerBody.Contains(m, StringComparison.Ordinal)))
        {
            messages.Add($"draft '{draftName}' rejected: injection heuristics");
            logger.LogWarning("Skill draft {Name} rejected by injection gate (session {Session}).", draftName, session.Id);
            return null;
        }

        // Gate d: quality judge (cheap model).
        var quality = await JudgeQualityAsync(draftName, description, body, ct).ConfigureAwait(false);
        if (quality.Score < MinQualityScore)
        {
            messages.Add($"draft '{draftName}' rejected by quality gate ({quality.Score:0.00}): {quality.Reason}");
            return null;
        }

        // Publish decision — AutomationMode applies to synthesized skills only.
        var autoPublish = opts.AutomationMode == SkillAutomationMode.AutoExecute
                          && !(untrustedFinal && opts.Security.UntrustedInputForcesReview);

        var result = await skillService.SaveAsync(new SaveSkillRequest(
            draftName,
            description,
            body,
            draft.WhenToUse,
            Publish: autoPublish,
            CreatedBy: "synthesis",
            Origin: SkillOrigin.Synthesized,
            GeneratorModel: llm.GetChat().GetService<ChatClientMetadata>()?.DefaultModelId,
            UntrustedInput: untrustedFinal,
            ChangeSummary: draft.ChangeSummary,
            Provenance: [new SkillProvenanceRef("harvested_session", session.Id.ToString())],
            AsCandidate: !autoPublish), ct).ConfigureAwait(false);

        if (!result.Success)
        {
            messages.Add($"draft '{draftName}' save failed: {result.Message}");
            return null;
        }

        (isUpdate || existing is not null ? updated : created).Add(draftName);
        messages.Add($"'{draftName}' → {result.Status} (confidence {delta.Confidence:0.00}, quality {quality.Score:0.00}{(untrustedFinal ? ", untrusted" : "")})");
        return draftName;
    }

    private async Task<CuratorVerdict> CurateAsync(SkillDelta delta, Skill nearMatch, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                """
                You curate a skill library. A proposed new skill overlaps an existing one.
                Decide: "update" (fold the new knowledge into the existing skill),
                "noop" (existing skill already covers it), or "add" (genuinely distinct).
                """),
            new(ChatRole.User,
                $"""
                EXISTING: {nearMatch.Name} — {nearMatch.Description}

                PROPOSED: {delta.Name} — {delta.Description}
                Outline: {delta.BodyOutline}
                """),
        };

        var chatOptions = BuildCheapModelOptions();
        var response = await llm.GetChat()
            .GetResponseAsync<CuratorVerdict>(messages, options: chatOptions, cancellationToken: ct)
            .ConfigureAwait(false);
        return response.Result ?? new CuratorVerdict("add", null, "curator unavailable — defaulting to add");
    }

    private const string DrafterSystemPrompt =
        """
        You write skills in the Agent Skills (SKILL.md) format. Rules:
        - description: third person, states what the skill does AND concrete triggers
          ("Use when ..."), max 1024 chars.
        - body: markdown, under 300 lines. Numbered checklist for workflows; include the
          verified commands/steps from the evidence; add a short "Pitfalls" section for
          the mistakes the session actually hit. Placeholders (<project>, <host>) instead
          of machine-specific values. No secrets. No shell-execution frontmatter tricks.
        - whenToUse: 1-2 sentences of extra trigger context (optional).
        - changeSummary: one line describing what this draft adds/changes.
        When updating an existing skill, integrate the new knowledge with a minimal
        delta — do NOT rewrite sections the new evidence doesn't touch.
        """;

    private async Task<SkillDraft> DraftAsync(SkillDelta delta, SessionTrace trace, Skill? existing, CancellationToken ct)
    {
        var evidence = delta.Evidence is { Count: > 0 }
            ? string.Join("\n", delta.Evidence.Select(e => $"- {e}"))
            : "(none provided)";

        var user = existing is null
            ? $"""
              Write a NEW skill.
              name: {delta.Name}
              description hint: {delta.Description}
              whenToUse hint: {delta.WhenToUse}
              outline: {delta.BodyOutline}
              evidence from the session:
              {evidence}
              """
            : $"""
              UPDATE an existing skill with new knowledge from a session.
              existing name: {existing.Name}
              existing description: {existing.Description}
              existing body:
              {existing.Body}

              new knowledge outline: {delta.BodyOutline}
              evidence:
              {evidence}
              """;

        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(options.Value.Synthesis.DrafterModelOverride))
        {
            chatOptions.ModelId = options.Value.Synthesis.DrafterModelOverride;
        }

        var response = await llm.GetChat()
            .GetResponseAsync<SkillDraft>(
                [new(ChatRole.System, DrafterSystemPrompt), new(ChatRole.User, user)],
                options: chatOptions,
                cancellationToken: ct)
            .ConfigureAwait(false);

        return response.Result
            ?? throw new InvalidOperationException("Drafter returned no parseable SkillDraft.");
    }

    private async Task<QualityVerdict> JudgeQualityAsync(string name, string description, string body, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                """
                Score a candidate skill 0..1 for a persistent agent skill library:
                - Actionable? (concrete steps, not vague advice)
                - Generalizable? (will recur; placeholders not machine-specific one-offs)
                - Non-obvious? (would a competent agent already know this? if yes, score low)
                - Coherent? (title/description/body agree)
                0.8+ excellent, 0.5 borderline, below 0.5 not worth keeping.
                """),
            new(ChatRole.User, $"name: {name}\ndescription: {description}\n\nbody:\n{body}"),
        };

        var response = await llm.GetChat()
            .GetResponseAsync<QualityVerdict>(messages, options: BuildCheapModelOptions(), cancellationToken: ct)
            .ConfigureAwait(false);
        return response.Result ?? new QualityVerdict(0.6, "judge unavailable — fail-open at borderline");
    }

    // ------------------------------------------------------------------ plumbing

    private ChatOptions BuildCheapModelOptions()
    {
        var chatOptions = new ChatOptions();
        if (!string.IsNullOrEmpty(options.Value.Synthesis.ReflectorModelOverride))
        {
            chatOptions.ModelId = options.Value.Synthesis.ReflectorModelOverride;
        }
        return chatOptions;
    }

    private async Task<string> BuildLibraryDigestAsync(CancellationToken ct)
    {
        var skills = await db.Skills.AsNoTracking()
            .Where(s => s.Status != SkillStatus.Deprecated && s.Status != SkillStatus.Rejected)
            .OrderByDescending(s => s.UsageCount)
            .Take(50)
            .Select(s => new { s.Name, s.Description })
            .ToListAsync(ct).ConfigureAwait(false);

        return skills.Count == 0
            ? "(library is empty)"
            : string.Join("\n", skills.Select(s => $"{s.Name}: {s.Description}"));
    }

    private async Task<List<Guid>> ClaimPendingAsync(int limit, CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var cmd = new NpgsqlCommand
        {
            Connection = conn,
            CommandText = """
                UPDATE memory.harvested_sessions
                SET status = 1
                WHERE id IN (
                    SELECT id FROM memory.harvested_sessions
                    WHERE status = 0
                    ORDER BY submitted_at
                    LIMIT @limit
                    FOR UPDATE SKIP LOCKED)
                RETURNING id
                """,
        };
        cmd.Parameters.AddWithValue("limit", limit);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    private async Task MarkAsync(Guid sessionId, HarvestStatus status, string? note, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var truncated = note is { Length: > 900 } ? note[..900] : note;
        await db.HarvestedSessions.Where(h => h.Id == sessionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.ProcessedAt, now)
                .SetProperty(x => x.Error, status is HarvestStatus.Failed or HarvestStatus.Skipped ? truncated : null), ct)
            .ConfigureAwait(false);
    }

    private static string Short(Guid id) => id.ToString("N")[..8];
}
