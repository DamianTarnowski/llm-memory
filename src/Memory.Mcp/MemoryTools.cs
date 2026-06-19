using System.ComponentModel;
using Memory.Domain;
using Memory.Pipeline;
using Memory.Pipeline.Reflection;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

[McpServerToolType]
public static class MemoryTools
{
    [McpServerTool(Name = "save_user_preference")]
    [Description("Save a durable user preference, habit, workflow correction, or 'do not do this again' instruction. " +
                 "Use proactively when the user corrects the agent strongly or repeatedly, especially with frustration. " +
                 "Do not save secrets or transient emotions; save the actionable preference.")]
    public static async Task<SaveEpisodeResponse> SaveUserPreferenceAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("Concise actionable preference to remember, e.g. 'Do not suggest GitHub Actions for this homelab unless explicitly asked.'")] string preference,
        [Description("Optional category, e.g. communication, workflow, coding_style, tooling, testing, privacy, deployment.")] string category = "workflow",
        [Description("Optional project/app/repo context this preference applies to.")] string? appliesTo = null,
        [Description("Optional short user quote or correction that triggered this preference. Keep it short; do not include secrets.")] string? triggerQuote = null,
        [Description("Optional rationale: why this preference matters.")] string? rationale = null,
        [Description("Strength from 1-5. Use 5 for explicit/frustrated corrections or repeated instructions.")] int strength = 4,
        [Description("Optional ISO 8601 timestamp of when this preference was observed (defaults to now).")] string? occurredAt = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        DateTimeOffset? when = null;
        if (!string.IsNullOrWhiteSpace(occurredAt) && DateTimeOffset.TryParse(occurredAt, out var parsed))
        {
            when = parsed;
        }

        var normalizedStrength = Math.Clamp(strength, 1, 5);
        var content = BuildPreferenceContent(preference, category, appliesTo, triggerQuote, rationale, normalizedStrength);
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = "user_preference",
            ["category"] = category,
            ["strength"] = normalizedStrength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(appliesTo)) metadata["applies_to"] = appliesTo;

        var result = await pipeline.IngestAsync(
            new IngestionRequest(
                "user_preference",
                content,
                when,
                metadata,
                ForceSave: true,
                MemoryTypeOverride: MemoryType.Preference,
                NoteKindOverride: NoteKind.Pattern,
                DirectNote: true,
                DeferEmbedding: true),
            ct).ConfigureAwait(false);

        return new SaveEpisodeResponse(
            EpisodeId: result.EpisodeId?.ToString() ?? "",
            NoteIds: result.Notes.Select(n => n.ToString()).ToArray(),
            EntityIds: result.EntitiesUpserted.Select(e => e.ToString()).ToArray(),
            Skipped: result.Skipped,
            SkipReason: result.SkipReason,
            ImportanceScore: result.ImportanceScore);
    }

    [McpServerTool(Name = "save_decision")]
    [Description("Save a durable project/product/architecture decision with rationale. Use for choices that future agents should respect.")]
    public static Task<SaveEpisodeResponse> SaveDecisionAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("The decision in one concise sentence.")] string decision,
        [Description("Why this decision was made.")] string? rationale = null,
        [Description("Optional rejected alternatives or tradeoffs.")] string? alternatives = null,
        [Description("Optional project/repo/app scope this decision applies to.")] string? appliesTo = null,
        [Description("Optional outcome/status, e.g. accepted, provisional, reverted, superseded.")] string? outcome = null,
        [Description("Optional ISO 8601 timestamp of when this decision was made.")] string? occurredAt = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var content = BuildStructuredContent("Decision", new Dictionary<string, string?>
        {
            ["Applies to"] = appliesTo,
            ["Decision"] = decision,
            ["Rationale"] = rationale,
            ["Alternatives / tradeoffs"] = alternatives,
            ["Outcome"] = outcome,
        });
        var metadata = BuildMetadata("decision", MemoryType.Semantic, NoteKind.Decision, appliesTo);
        AddIf(metadata, "outcome", outcome);
        return IngestTypedAsync(pipeline, "decision", content, occurredAt, metadata, MemoryType.Semantic, NoteKind.Decision, ct);
    }

    [McpServerTool(Name = "save_coding_pattern")]
    [Description("Save a reusable coding/workflow pattern, convention, gotcha, or test strategy for future agents.")]
    public static Task<SaveEpisodeResponse> SaveCodingPatternAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("Short name or summary of the pattern.")] string pattern,
        [Description("Problem/context where the pattern applies.")] string? problem = null,
        [Description("Recommended solution or implementation approach.")] string? solution = null,
        [Description("Optional language/framework/tooling scope, e.g. Blazor .NET 10, EF Core, Playwright.")] string? appliesTo = null,
        [Description("Optional short example or command. Do not include secrets.")] string? example = null,
        [Description("Optional rationale or why this pattern matters.")] string? rationale = null,
        [Description("Optional ISO 8601 timestamp.")] string? occurredAt = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var content = BuildStructuredContent("Coding pattern", new Dictionary<string, string?>
        {
            ["Applies to"] = appliesTo,
            ["Pattern"] = pattern,
            ["Problem"] = problem,
            ["Solution"] = solution,
            ["Example"] = example,
            ["Rationale"] = rationale,
        });
        var metadata = BuildMetadata("coding_pattern", MemoryType.Procedural, NoteKind.Pattern, appliesTo);
        return IngestTypedAsync(pipeline, "coding_pattern", content, occurredAt, metadata, MemoryType.Procedural, NoteKind.Pattern, ct);
    }

    [McpServerTool(Name = "save_ui_test_finding")]
    [Description("Save a UI/testing finding: visual defect, Playwright result, accessibility issue, layout problem, or regression.")]
    public static Task<SaveEpisodeResponse> SaveUiTestFindingAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("App/project/page where the finding occurred.")] string app,
        [Description("The observed issue or test result.")] string finding,
        [Description("Severity: info, low, medium, high, critical.")] string severity = "medium",
        [Description("Optional reproduction steps.")] string? reproductionSteps = null,
        [Description("Optional expected behavior.")] string? expected = null,
        [Description("Optional actual behavior.")] string? actual = null,
        [Description("Optional tool/evidence, e.g. Playwright screenshot path, axe, browser console.")] string? evidence = null,
        [Description("Optional ISO 8601 timestamp.")] string? occurredAt = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var content = BuildStructuredContent("UI test finding", new Dictionary<string, string?>
        {
            ["App / page"] = app,
            ["Severity"] = severity,
            ["Finding"] = finding,
            ["Reproduction"] = reproductionSteps,
            ["Expected"] = expected,
            ["Actual"] = actual,
            ["Evidence"] = evidence,
        });
        var metadata = BuildMetadata("ui_test_finding", MemoryType.Episodic, NoteKind.Error, app);
        metadata["severity"] = severity;
        return IngestTypedAsync(pipeline, "ui_test_finding", content, occurredAt, metadata, MemoryType.Episodic, NoteKind.Error, ct);
    }

    [McpServerTool(Name = "save_debug_finding")]
    [Description("Save a debugging/deploy finding: symptom, root cause, fix, command evidence, or operational lesson.")]
    public static Task<SaveEpisodeResponse> SaveDebugFindingAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("Project/app/service/repo where this applies.")] string scope,
        [Description("Symptom or problem observed.")] string symptom,
        [Description("Root cause, if known.")] string? rootCause = null,
        [Description("Fix or workaround.")] string? fix = null,
        [Description("Evidence: log line, command result summary, test result. Do not include secrets.")] string? evidence = null,
        [Description("Optional lesson for future agents.")] string? lesson = null,
        [Description("Optional ISO 8601 timestamp.")] string? occurredAt = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var kind = string.IsNullOrWhiteSpace(rootCause) && string.IsNullOrWhiteSpace(fix)
            ? NoteKind.Observation
            : NoteKind.Learning;
        var content = BuildStructuredContent("Debug finding", new Dictionary<string, string?>
        {
            ["Scope"] = scope,
            ["Symptom"] = symptom,
            ["Root cause"] = rootCause,
            ["Fix / workaround"] = fix,
            ["Evidence"] = evidence,
            ["Lesson"] = lesson,
        });
        var metadata = BuildMetadata("debug_finding", MemoryType.Episodic, kind, scope);
        return IngestTypedAsync(pipeline, "debug_finding", content, occurredAt, metadata, MemoryType.Episodic, kind, ct);
    }

    [McpServerTool(Name = "save_episode")]
    [Description("Save a raw episode (chat turn, document chunk, observation) to the active project's memory. " +
                 "Returns the episode id and any notes/entities extracted.")]
    public static async Task<SaveEpisodeResponse> SaveEpisodeAsync(
        IIngestionPipeline pipeline,
        ITenantContext tenant,
        [Description("Logical source label, e.g. 'chat', 'doc', 'observation'.")] string source,
        [Description("The full text content of the episode.")] string content,
        [Description("Optional ISO 8601 timestamp of when this episode actually occurred (defaults to now).")] string? occurredAt = null,
        [Description("Optional memory type override: semantic, episodic, procedural, preference, document, reflection.")] string? memoryType = null,
        [Description("Optional note kind override: general, observation, decision, learning, error, pattern.")] string? kind = null,
        [Description("Force saving and bypass optional importance filter. Use only for explicit user/agent saves.")] bool forceSave = false,
        [Description("When true, store the content as one explicit note without LLM extraction. Use for already-curated agent/user material.")] bool directNote = false,
        [Description("When true, return after saving the note and let a background worker create the embedding. Defaults to directNote.")] bool? deferEmbedding = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        DateTimeOffset? when = null;
        if (!string.IsNullOrWhiteSpace(occurredAt) && DateTimeOffset.TryParse(occurredAt, out var parsed))
        {
            when = parsed;
        }

        var result = await pipeline.IngestAsync(
            new IngestionRequest(
                source,
                content,
                when,
                ForceSave: forceSave,
                MemoryTypeOverride: ParseEnum<MemoryType>(memoryType),
                NoteKindOverride: ParseEnum<NoteKind>(kind),
                DirectNote: directNote,
                DeferEmbedding: deferEmbedding ?? directNote),
            ct);
        return new SaveEpisodeResponse(
            EpisodeId: result.EpisodeId?.ToString() ?? "",
            NoteIds: result.Notes.Select(n => n.ToString()).ToArray(),
            EntityIds: result.EntitiesUpserted.Select(e => e.ToString()).ToArray(),
            Skipped: result.Skipped,
            SkipReason: result.SkipReason,
            ImportanceScore: result.ImportanceScore);
    }

    [McpServerTool(Name = "search_memory")]
    [Description("Hybrid search across the active project's memory (vector + graph + temporal). " +
                 "Returns ranked notes with their related entities.")]
    public static async Task<SearchMemoryResponse> SearchMemoryAsync(
        ISearchPipeline pipeline,
        ITenantContext tenant,
        [Description("Natural language query.")] string query,
        [Description("Maximum number of hits to return (default 20).")] int maxResults = 20,
        [Description("Optional caller label, e.g. 'codex', 'claude-code', 'devhub-opus'.")] string? caller = null,
        [Description("Optional active project/repo/app label for routing and provenance.")] string? activeProject = null,
        [Description("Optional compact conversation/task summary used to rewrite vague follow-up queries.")] string? conversationSummary = null,
        [Description("Optional current topic, e.g. 'LegalAssistant RAG lessons'.")] string? currentTopic = null,
        [Description("Smart-caller route mode: memory_light, memory_medium, heavy_rag, graph_rag, document_rag, no_rag, write_memory.")] string? mode = null,
        [Description("Smart-caller standalone query. Use this for vague follow-ups like 'powiedz o tym więcej'.")] string? standaloneQuery = null,
        [Description("Smart-caller query type label, e.g. follow_up, factual, semantic, relational, document, coding_pattern.")] string? queryType = null,
        [Description("Optional memory type filter, separated by newlines or pipes: semantic, episodic, procedural, preference, document, reflection.")] string? memoryTypes = null,
        [Description("Optional alternate query variants separated by newlines or pipes.")] string? variants = null,
        [Description("Whether to use vector search.")] bool? useVectorSearch = null,
        [Description("Whether to use BM25/full-text search.")] bool? useBm25Search = null,
        [Description("Whether to use graph retrieval.")] bool? useGraph = null,
        [Description("Whether to use reranking.")] bool? useReranker = null,
        [Description("Whether to use query expansion.")] bool? useQueryExpansion = null,
        [Description("Whether to use image-vector search if configured.")] bool? useImageSearch = null,
        [Description("Vector stream weight multiplier, default 1.0.")] double? vectorWeight = null,
        [Description("BM25/full-text stream weight multiplier, default 1.0.")] double? bm25Weight = null,
        [Description("Graph stream weight multiplier, default 1.0.")] double? graphWeight = null,
        [Description("Image-vector stream weight multiplier, default 1.0.")] double? imageWeight = null,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        var context = new SearchContext(
            Caller: caller ?? "mcp",
            ActiveProject: activeProject,
            ConversationSummary: conversationSummary,
            CurrentTopic: currentTopic);

        var routeOverride = BuildSmartCallerRoute(
            query,
            maxResults,
            mode,
            standaloneQuery,
            queryType,
            variants,
            useVectorSearch,
            useBm25Search,
            useGraph,
            useReranker,
            useQueryExpansion,
            useImageSearch,
            vectorWeight,
            bm25Weight,
            graphWeight,
            imageWeight);

        var result = await pipeline.SearchAsync(
            new SearchRequest(
                query,
                maxResults,
                Context: context,
                RouteOverride: routeOverride,
                MemoryTypes: ParseEnumList<MemoryType>(memoryTypes)),
            ct);
        return new SearchMemoryResponse(
            result.Hits.Select(h => new SearchMemoryHit(
                h.NoteId.ToString(),
                h.Content,
                h.Score,
                h.RelatedEntities.Select(e => e.ToString()).ToArray())).ToArray(),
            result.TotalCandidates,
            result.Route);
    }

    [McpServerTool(Name = "supersede_note")]
    [Description("Mark a memory note as superseded so it stops appearing in retrieval. Use when a newer fact/decision replaces it or it is a confirmed duplicate.")]
    public static async Task<SupersedeNoteResponse> SupersedeNoteAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        TimeProvider time,
        [Description("Note id (uuid) to supersede.")] string noteId,
        [Description("Short audit reason for superseding the note.")] string reason,
        [Description("Optional replacement note id (uuid) that supersedes this note.")] string? replacementNoteId = null,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();
        if (!Guid.TryParse(noteId, out var guid))
        {
            return new SupersedeNoteResponse(false, noteId, "invalid note id");
        }

        var now = time.GetUtcNow();
        var id = new NoteId(guid);
        var updated = await db.Notes
            .Where(n => n.Id == id && n.SupersededAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.SupersededAt, _ => now), ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            return new SupersedeNoteResponse(false, noteId, "note not found or already superseded");
        }

        NoteId? replacement = null;
        if (Guid.TryParse(replacementNoteId, out var replacementGuid))
        {
            replacement = new NoteId(replacementGuid);
            var exists = await db.Notes.AnyAsync(n => n.Id == replacement, ct).ConfigureAwait(false);
            if (exists)
            {
                var relationExists = await db.NoteRelations
                    .AnyAsync(r => r.NoteId == id && r.RelatedNoteId == replacement, ct)
                    .ConfigureAwait(false);
                if (!relationExists)
                {
                    db.NoteRelations.Add(new NoteRelation
                    {
                        NoteId = id,
                        RelatedNoteId = replacement.Value,
                        Project = scope.Project,
                        RelationType = "superseded-by",
                        Confidence = 1.0,
                        Similarity = 1.0,
                        Description = reason,
                        CreatedAt = now,
                    });
                }
            }
        }

        await WriteAuditEpisodeAsync(db, scope.Project, "supersede_note", reason, now, new Dictionary<string, string>
        {
            ["note_id"] = noteId,
            ["replacement_note_id"] = replacement?.ToString() ?? "",
        }, ct).ConfigureAwait(false);

        return new SupersedeNoteResponse(true, noteId, "superseded");
    }

    [McpServerTool(Name = "invalidate_graph_edge")]
    [Description("Invalidate a bad or outdated graph edge. Use when an entity relationship is confirmed wrong or obsolete.")]
    public static async Task<InvalidateEdgeResponse> InvalidateGraphEdgeAsync(
        IGraphContext graph,
        MemoryDbContext db,
        ITenantContext tenant,
        TimeProvider time,
        [Description("Edge id (uuid) to invalidate.")] string edgeId,
        [Description("Short audit reason for invalidating the edge.")] string reason,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();
        if (!Guid.TryParse(edgeId, out var guid))
        {
            return new InvalidateEdgeResponse(false, edgeId, "invalid edge id");
        }

        var id = new EdgeId(guid);
        var existing = await graph.GetEdgesAsync(scope.Project, ct: ct).ConfigureAwait(false);
        if (!existing.Any(e => e.Id == id && e.InvalidatedAt is null))
        {
            return new InvalidateEdgeResponse(false, edgeId, "edge not found or already invalidated");
        }

        var now = time.GetUtcNow();
        await graph.InvalidateEdgeAsync(id, now, ct).ConfigureAwait(false);
        await WriteAuditEpisodeAsync(db, scope.Project, "invalidate_graph_edge", reason, now, new Dictionary<string, string>
        {
            ["edge_id"] = edgeId,
        }, ct).ConfigureAwait(false);

        return new InvalidateEdgeResponse(true, edgeId, "invalidated");
    }

    [McpServerTool(Name = "list_memory_hygiene")]
    [Description("Return a small hygiene report: superseded notes, duplicate/supersedence relations, and old reflections. Use before cleanup or audits.")]
    public static async Task<MemoryHygieneResponse> ListMemoryHygieneAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        [Description("Maximum rows per section (default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var take = Math.Clamp(limit <= 0 ? 20 : limit, 1, 100);

        var supersededRows = await db.Notes
            .Where(n => n.SupersededAt != null)
            .OrderByDescending(n => n.SupersededAt)
            .Take(take)
            .Select(n => new { n.Id, n.Content, n.Kind, n.MemoryType, n.CreatedAt, n.SupersededAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var superseded = supersededRows
            .Select(n => new HygieneNoteView(
                n.Id.ToString(),
                n.Content,
                n.Kind.ToString(),
                n.MemoryType.ToString(),
                n.CreatedAt.ToString("o"),
                n.SupersededAt!.Value.ToString("o")))
            .ToArray();

        var duplicateRelationRows = await db.NoteRelations
            .Where(r => r.RelationType == "duplicates" || r.RelationType == "superseded-by")
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Select(r => new { r.NoteId, r.RelatedNoteId, r.RelationType, r.Confidence, r.Description, r.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var duplicateRelations = duplicateRelationRows
            .Select(r => new HygieneRelationView(
                r.NoteId.ToString(),
                r.RelatedNoteId.ToString(),
                r.RelationType,
                r.Confidence,
                r.Description,
                r.CreatedAt.ToString("o")))
            .ToArray();

        var oldReflectionRows = await db.Reflections
            .OrderBy(r => r.GeneratedAt)
            .Take(take)
            .Select(r => new { r.Id, r.Scope, r.GeneratedAt, r.Summary })
            .ToListAsync(ct).ConfigureAwait(false);
        var oldReflections = oldReflectionRows
            .Select(r => new HygieneReflectionView(
                r.Id.ToString(),
                r.Scope,
                r.GeneratedAt.ToString("o"),
                r.Summary.Length > 240 ? r.Summary[..240] + "..." : r.Summary))
            .ToArray();

        return new MemoryHygieneResponse(superseded, duplicateRelations, oldReflections);
    }

    private static async Task<SaveEpisodeResponse> IngestTypedAsync(
        IIngestionPipeline pipeline,
        string source,
        string content,
        string? occurredAt,
        Dictionary<string, string> metadata,
        MemoryType memoryType,
        NoteKind noteKind,
        CancellationToken ct)
    {
        var result = await pipeline.IngestAsync(
            new IngestionRequest(
                source,
                content,
                ParseTimestamp(occurredAt),
                metadata,
                ForceSave: true,
                MemoryTypeOverride: memoryType,
                NoteKindOverride: noteKind,
                DirectNote: true,
                DeferEmbedding: true),
            ct).ConfigureAwait(false);

        return new SaveEpisodeResponse(
            EpisodeId: result.EpisodeId?.ToString() ?? "",
            NoteIds: result.Notes.Select(n => n.ToString()).ToArray(),
            EntityIds: result.EntitiesUpserted.Select(e => e.ToString()).ToArray(),
            Skipped: result.Skipped,
            SkipReason: result.SkipReason,
            ImportanceScore: result.ImportanceScore);
    }

    private static string BuildStructuredContent(string title, IReadOnlyDictionary<string, string?> fields)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(title.TrimEnd('.') + ".");
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            sb.Append(key).Append(": ").AppendLine(value.Trim());
        }
        sb.AppendLine("This is an explicit typed memory saved by an agent or user workflow.");
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildMetadata(string kind, MemoryType memoryType, NoteKind noteKind, string? appliesTo)
    {
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = kind,
            ["memory_type"] = memoryType.ToString(),
            ["note_kind"] = noteKind.ToString(),
            ["explicit_save"] = "true",
        };
        AddIf(metadata, "applies_to", appliesTo);
        return metadata;
    }

    private static void AddIf(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value.Trim();
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, out var parsed)
            ? parsed
            : null;

    private static TEnum? ParseEnum<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().Replace("-", "_", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name, ignoreCase: true);
            }
        }
        return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<TEnum>? ParseEnumList<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var values = raw
            .Split(new[] { '\n', '|', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEnum<TEnum>)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static async Task WriteAuditEpisodeAsync(
        MemoryDbContext db,
        ProjectId project,
        string action,
        string reason,
        DateTimeOffset now,
        Dictionary<string, string> metadata,
        CancellationToken ct)
    {
        metadata["kind"] = "memory_hygiene";
        metadata["action"] = action;
        metadata["memory_type"] = MemoryType.Episodic.ToString();

        db.Episodes.Add(new Episode
        {
            Id = EpisodeId.New(),
            Project = project,
            Source = "memory_hygiene",
            Content = $"{action}: {reason}",
            OccurredAt = now,
            IngestedAt = now,
            Metadata = metadata,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string BuildPreferenceContent(
        string preference,
        string category,
        string? appliesTo,
        string? triggerQuote,
        string? rationale,
        int strength)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Durable user preference / correction.");
        sb.AppendLine($"Category: {category}");
        sb.AppendLine($"Strength: {strength}/5");
        if (!string.IsNullOrWhiteSpace(appliesTo))
        {
            sb.AppendLine($"Applies to: {appliesTo}");
        }
        sb.AppendLine($"Preference: {preference.Trim()}");
        if (!string.IsNullOrWhiteSpace(rationale))
        {
            sb.AppendLine($"Rationale: {rationale.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(triggerQuote))
        {
            sb.AppendLine($"Trigger quote: {triggerQuote.Trim()}");
        }
        sb.AppendLine("Future agents should treat this as an operational preference, not as a transient chat message.");
        return sb.ToString();
    }

    private static QueryRoute? BuildSmartCallerRoute(
        string query,
        int maxResults,
        string? mode,
        string? standaloneQuery,
        string? queryType,
        string? variants,
        bool? useVectorSearch,
        bool? useBm25Search,
        bool? useGraph,
        bool? useReranker,
        bool? useQueryExpansion,
        bool? useImageSearch,
        double? vectorWeight,
        double? bm25Weight,
        double? graphWeight,
        double? imageWeight)
    {
        var hasAnyRouteParameter =
            !string.IsNullOrWhiteSpace(mode) ||
            !string.IsNullOrWhiteSpace(standaloneQuery) ||
            !string.IsNullOrWhiteSpace(queryType) ||
            !string.IsNullOrWhiteSpace(variants) ||
            useVectorSearch.HasValue ||
            useBm25Search.HasValue ||
            useGraph.HasValue ||
            useReranker.HasValue ||
            useQueryExpansion.HasValue ||
            useImageSearch.HasValue ||
            vectorWeight.HasValue ||
            bm25Weight.HasValue ||
            graphWeight.HasValue ||
            imageWeight.HasValue;

        if (!hasAnyRouteParameter) return null;

        var parsedVariants = string.IsNullOrWhiteSpace(variants)
            ? Array.Empty<string>()
            : variants
                .Split(new[] { '\n', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Take(5)
                .ToArray();

        return new QueryRoute(
            ShouldSearch: true,
            Mode: QueryRoute.ParseMode(mode),
            StandaloneQuery: string.IsNullOrWhiteSpace(standaloneQuery) ? query : standaloneQuery,
            QueryType: queryType ?? "smart_caller",
            MaxResults: maxResults,
            Variants: parsedVariants,
            UseVectorSearch: useVectorSearch ?? true,
            UseBm25Search: useBm25Search ?? true,
            UseGraph: useGraph ?? true,
            UseReranker: useReranker ?? true,
            UseQueryExpansion: useQueryExpansion ?? true,
            UseImageSearch: useImageSearch ?? true,
            VectorWeight: vectorWeight ?? 1.0,
            Bm25Weight: bm25Weight ?? 1.0,
            GraphWeight: graphWeight ?? 1.0,
            ImageWeight: imageWeight ?? 1.0,
            RouterModel: "smart-caller");
    }

    [McpServerTool(Name = "reflect")]
    [Description("Synthesize recent notes into a reflection (key themes, tensions, actionable insights). " +
                 "Stored as a Reflection record and returned as a summary string.")]
    public static async Task<ReflectMemoryResponse> ReflectAsync(
        IReflectionPipeline pipeline,
        ITenantContext tenant,
        [Description("Logical scope label, e.g. 'recent', 'weekly', 'project-X'.")] string scope = "recent",
        [Description("Maximum number of notes to consider (default 30).")] int maxNotes = 30,
        CancellationToken ct = default)
    {
        _ = tenant.Require();

        var result = await pipeline.ReflectAsync(new ReflectionRequest(scope, maxNotes), ct);
        return new ReflectMemoryResponse(
            result.Id.ToString(),
            result.Scope,
            result.Summary,
            result.NotesConsidered);
    }

    [McpServerTool(Name = "find_related_notes")]
    [Description("Return note-to-note relations (auto-linked at ingest time via A-MEM). " +
                 "Useful for exploring conceptual neighbours, contradictions, or specializations of a known note.")]
    public static async Task<FindRelatedNotesResponse> FindRelatedNotesAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        [Description("Note id (uuid) of the source note.")] string noteId,
        [Description("Maximum number of relations to return (default 25).")] int maxResults = 25,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        if (!Guid.TryParse(noteId, out var guid))
        {
            return new FindRelatedNotesResponse(Array.Empty<RelatedNoteView>());
        }

        var nid = new NoteId(guid);
        var rows = await db.NoteRelations
            .Where(r => r.NoteId == nid)
            .OrderByDescending(r => r.Confidence)
            .Take(maxResults)
            .Join(db.Notes,
                r => r.RelatedNoteId,
                n => n.Id,
                (r, n) => new RelatedNoteView(
                    n.Id.ToString(),
                    n.Content,
                    r.RelationType,
                    r.Confidence,
                    r.Similarity,
                    r.Description))
            .ToListAsync(ct).ConfigureAwait(false);

        return new FindRelatedNotesResponse(rows);
    }

    [McpServerTool(Name = "get_entity")]
    [Description("Fetch a single entity by canonical name and its 1-hop neighbours in the knowledge graph " +
                 "(both outgoing and incoming edges). Returns null if not found.")]
    public static async Task<GetEntityResponse> GetEntityAsync(
        IGraphContext graph,
        ITenantContext tenant,
        [Description("Entity name (canonical, usually lowercase, hyphen-separated for multi-word).")] string name,
        [Description("Maximum number of edges to return per direction (default 25).")] int maxEdges = 25,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();

        var matches = await graph.GetEntitiesAsync(scope.Project, nameFilter: name, limit: 1, ct);
        if (matches.Count == 0)
        {
            return new GetEntityResponse(null, Array.Empty<RelatedEdgeView>(), Array.Empty<RelatedEdgeView>());
        }

        var entity = matches[0];
        var outgoing = await graph.GetEdgesAsync(scope.Project, from: entity.Id, ct: ct);
        var incoming = await graph.GetEdgesAsync(scope.Project, to: entity.Id, ct: ct);

        var entityView = new EntityView(
            entity.Id.ToString(),
            entity.Name,
            entity.Kind,
            new Dictionary<string, string>(entity.Attributes),
            entity.FirstSeenAt.ToString("o"),
            entity.LastSeenAt.ToString("o"));

        return new GetEntityResponse(
            entityView,
            outgoing.Take(maxEdges).Select(e => RelatedEdgeView.From(e, otherEntity: e.To)).ToArray(),
            incoming.Take(maxEdges).Select(e => RelatedEdgeView.From(e, otherEntity: e.From)).ToArray());
    }
}

public sealed record SaveEpisodeResponse(
    string EpisodeId,
    IReadOnlyList<string> NoteIds,
    IReadOnlyList<string> EntityIds,
    bool Skipped = false,
    string? SkipReason = null,
    double? ImportanceScore = null);

public sealed record ReflectMemoryResponse(
    string ReflectionId,
    string Scope,
    string Summary,
    int NotesConsidered);

public sealed record RelatedNoteView(
    string NoteId,
    string Content,
    string RelationType,
    double Confidence,
    double Similarity,
    string? Description);

public sealed record FindRelatedNotesResponse(
    IReadOnlyList<RelatedNoteView> Relations);

public sealed record SupersedeNoteResponse(
    bool Success,
    string NoteId,
    string Message);

public sealed record InvalidateEdgeResponse(
    bool Success,
    string EdgeId,
    string Message);

public sealed record HygieneNoteView(
    string NoteId,
    string Content,
    string Kind,
    string MemoryType,
    string CreatedAt,
    string SupersededAt);

public sealed record HygieneRelationView(
    string NoteId,
    string RelatedNoteId,
    string RelationType,
    double Confidence,
    string? Description,
    string CreatedAt);

public sealed record HygieneReflectionView(
    string ReflectionId,
    string Scope,
    string GeneratedAt,
    string SummaryPreview);

public sealed record MemoryHygieneResponse(
    IReadOnlyList<HygieneNoteView> SupersededNotes,
    IReadOnlyList<HygieneRelationView> DuplicateOrSupersedenceRelations,
    IReadOnlyList<HygieneReflectionView> OldestReflections);

public sealed record SearchMemoryHit(
    string NoteId,
    string Content,
    double Score,
    IReadOnlyList<string> RelatedEntityIds);

public sealed record SearchMemoryResponse(
    IReadOnlyList<SearchMemoryHit> Hits,
    int TotalCandidates,
    SearchRouteTrace? Route);

public sealed record EntityView(
    string Id,
    string Name,
    string Kind,
    Dictionary<string, string> Attributes,
    string FirstSeenAt,
    string LastSeenAt);

public sealed record RelatedEdgeView(
    string EdgeId,
    string OtherEntityId,
    string Relation,
    string RecordedAt,
    string? ValidFrom,
    string? ValidTo,
    string? InvalidatedAt)
{
    internal static RelatedEdgeView From(Memory.Domain.Edge e, Memory.Domain.EntityId otherEntity) => new(
        e.Id.ToString(),
        otherEntity.ToString(),
        e.Relation,
        e.RecordedAt.ToString("o"),
        e.ValidFrom?.ToString("o"),
        e.ValidTo?.ToString("o"),
        e.InvalidatedAt?.ToString("o"));
}

public sealed record GetEntityResponse(
    EntityView? Entity,
    IReadOnlyList<RelatedEdgeView> OutgoingEdges,
    IReadOnlyList<RelatedEdgeView> IncomingEdges);
