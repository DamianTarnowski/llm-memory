using System.ComponentModel;
using System.Text.Json;
using Memory.Domain;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

[McpServerResourceType]
public static class MemoryResources
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [McpServerResource(UriTemplate = "memory://project", Name = "Project summary")]
    [Description("Summary of the active project: counts of episodes, notes, entities, edges, reflections.")]
    public static async Task<TextResourceContents> GetProjectSummaryAsync(
        MemoryDbContext db,
        IGraphContext graph,
        ITenantContext tenant,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var episodes = await db.Episodes.CountAsync(ct).ConfigureAwait(false);
        var notes = await db.Notes.Where(n => n.SupersededAt == null).CountAsync(ct).ConfigureAwait(false);
        var supersededNotes = await db.Notes.Where(n => n.SupersededAt != null).CountAsync(ct).ConfigureAwait(false);
        var reflections = await db.Reflections.CountAsync(ct).ConfigureAwait(false);
        var memoryTypeCountsRaw = await db.Notes
            .Where(n => n.SupersededAt == null)
            .GroupBy(n => n.MemoryType)
            .Select(g => new { memoryType = g.Key, count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        var entities = await graph.GetEntitiesAsync(scope.Project, limit: 10000, ct: ct).ConfigureAwait(false);
        var summary = new
        {
            project_id = scope.Project.Value,
            organization_id = scope.Organization.Value,
            counts = new
            {
                episodes,
                active_notes = notes,
                superseded_notes = supersededNotes,
                entities = entities.Count,
                reflections,
            },
            active_notes_by_memory_type = memoryTypeCountsRaw
                .OrderBy(x => x.memoryType.ToString())
                .ToDictionary(x => x.memoryType.ToString(), x => x.count),
            top_entities_by_recency = entities
                .OrderByDescending(e => e.LastSeenAt)
                .Take(10)
                .Select(e => new { e.Name, e.Kind, lastSeenAt = e.LastSeenAt })
                .ToArray(),
        };

        return new TextResourceContents
        {
            Uri = "memory://project",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(summary, JsonOpts),
        };
    }

    [McpServerResource(UriTemplate = "memory://core/project-profile", Name = "Core project profile")]
    [Description("Compact startup context for the active project: memory-type counts, recent decisions, procedures, preferences, and latest reflection.")]
    public static async Task<TextResourceContents> GetCoreProjectProfileAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var typeCounts = await db.Notes
            .Where(n => n.SupersededAt == null)
            .GroupBy(n => n.MemoryType)
            .Select(g => new { memoryType = g.Key, count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var kindCounts = await db.Notes
            .Where(n => n.SupersededAt == null)
            .GroupBy(n => n.Kind)
            .Select(g => new { kind = g.Key, count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var latestReflection = await db.Reflections
            .OrderByDescending(r => r.GeneratedAt)
            .Select(r => new { r.Scope, r.GeneratedAt, r.Summary })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var recentDecisions = await LoadNotePayloadsAsync(
            db.Notes.Where(n => n.SupersededAt == null && n.Kind == NoteKind.Decision),
            8,
            ct).ConfigureAwait(false);
        var procedures = await LoadNotePayloadsAsync(
            db.Notes.Where(n => n.SupersededAt == null && (n.MemoryType == MemoryType.Procedural || n.Kind == NoteKind.Pattern)),
            8,
            ct).ConfigureAwait(false);
        var preferences = await LoadNotePayloadsAsync(
            db.Notes.Where(n => n.SupersededAt == null && n.MemoryType == MemoryType.Preference),
            8,
            ct).ConfigureAwait(false);

        var payload = new
        {
            project_id = scope.Project.Value,
            organization_id = scope.Organization.Value,
            active_notes_by_memory_type = typeCounts
                .OrderBy(x => x.memoryType.ToString())
                .ToDictionary(x => x.memoryType.ToString(), x => x.count),
            active_notes_by_kind = kindCounts
                .OrderBy(x => x.kind.ToString())
                .ToDictionary(x => x.kind.ToString(), x => x.count),
            recent_decisions = recentDecisions,
            procedural_memory = procedures,
            user_preferences = preferences,
            latest_reflection = latestReflection is null ? null : new
            {
                latestReflection.Scope,
                latestReflection.GeneratedAt,
                Summary = latestReflection.Summary.Length > 1200 ? latestReflection.Summary[..1200] + "..." : latestReflection.Summary,
            },
        };

        return new TextResourceContents
        {
            Uri = "memory://core/project-profile",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(payload, JsonOpts),
        };
    }

    [McpServerResource(UriTemplate = "memory://core/user-preferences", Name = "Core user preferences")]
    [Description("Active durable user/team preferences and corrections for the current project.")]
    public static async Task<TextResourceContents> GetCoreUserPreferencesAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var notes = await LoadNotePayloadsAsync(
            db.Notes.Where(n => n.SupersededAt == null && n.MemoryType == MemoryType.Preference),
            50,
            ct).ConfigureAwait(false);

        return new TextResourceContents
        {
            Uri = "memory://core/user-preferences",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(new { notes }, JsonOpts),
        };
    }

    [McpServerResource(UriTemplate = "memory://core/recent-decisions", Name = "Core recent decisions")]
    [Description("Recent active decisions in the current project.")]
    public static async Task<TextResourceContents> GetCoreRecentDecisionsAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var notes = await LoadNotePayloadsAsync(
            db.Notes.Where(n => n.SupersededAt == null && n.Kind == NoteKind.Decision),
            50,
            ct).ConfigureAwait(false);

        return new TextResourceContents
        {
            Uri = "memory://core/recent-decisions",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(new { notes }, JsonOpts),
        };
    }

    [McpServerResource(UriTemplate = "memory://core/procedures", Name = "Core procedures and patterns")]
    [Description("Procedural memories: coding patterns, workflows, checklists, and gotchas.")]
    public static async Task<TextResourceContents> GetCoreProceduresAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var notes = await LoadNotePayloadsAsync(
            db.Notes.Where(n => n.SupersededAt == null && (n.MemoryType == MemoryType.Procedural || n.Kind == NoteKind.Pattern)),
            50,
            ct).ConfigureAwait(false);

        return new TextResourceContents
        {
            Uri = "memory://core/procedures",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(new { notes }, JsonOpts),
        };
    }

    [McpServerResource(UriTemplate = "memory://entity/{name}", Name = "Entity")]
    [Description("Single entity by canonical name + 1-hop neighbours in the graph.")]
    public static async Task<TextResourceContents> GetEntityResourceAsync(
        IGraphContext graph,
        ITenantContext tenant,
        string name,
        CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var matches = await graph.GetEntitiesAsync(scope.Project, nameFilter: name, limit: 1, ct).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return new TextResourceContents
            {
                Uri = $"memory://entity/{name}",
                MimeType = "application/json",
                Text = "null",
            };
        }
        var entity = matches[0];
        var outgoing = await graph.GetEdgesAsync(scope.Project, from: entity.Id, ct: ct).ConfigureAwait(false);
        var incoming = await graph.GetEdgesAsync(scope.Project, to: entity.Id, ct: ct).ConfigureAwait(false);
        var payload = new
        {
            entity = new
            {
                id = entity.Id.Value,
                name = entity.Name,
                kind = entity.Kind,
                attributes = entity.Attributes,
                firstSeenAt = entity.FirstSeenAt,
                lastSeenAt = entity.LastSeenAt,
            },
            outgoing = outgoing.Take(50).Select(e => new
            {
                relation = e.Relation,
                toEntityId = e.To.Value,
                recordedAt = e.RecordedAt,
                validFrom = e.ValidFrom,
                validTo = e.ValidTo,
                invalidatedAt = e.InvalidatedAt,
            }).ToArray(),
            incoming = incoming.Take(50).Select(e => new
            {
                relation = e.Relation,
                fromEntityId = e.From.Value,
                recordedAt = e.RecordedAt,
                validFrom = e.ValidFrom,
                validTo = e.ValidTo,
                invalidatedAt = e.InvalidatedAt,
            }).ToArray(),
        };
        return new TextResourceContents
        {
            Uri = $"memory://entity/{name}",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(payload, JsonOpts),
        };
    }

    [McpServerResource(UriTemplate = "memory://note/{id}", Name = "Note")]
    [Description("Single note by id including its source episode, keywords, and tags.")]
    public static async Task<TextResourceContents> GetNoteResourceAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        string id,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        if (!Guid.TryParse(id, out var guid))
        {
            return new TextResourceContents
            {
                Uri = $"memory://note/{id}",
                MimeType = "application/json",
                Text = "{\"error\":\"invalid uuid\"}",
            };
        }
        var nid = new NoteId(guid);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == nid, ct).ConfigureAwait(false);
        if (note is null)
        {
            return new TextResourceContents
            {
                Uri = $"memory://note/{id}",
                MimeType = "application/json",
                Text = "null",
            };
        }
        var payload = new
        {
            id = note.Id.Value,
            content = note.Content,
            contextDescription = note.ContextDescription,
            keywords = note.Keywords,
            tags = note.Tags,
            kind = note.Kind.ToString(),
            memoryType = note.MemoryType.ToString(),
            createdAt = note.CreatedAt,
            supersededAt = note.SupersededAt,
            sourceEpisodeId = note.SourceEpisode?.Value,
        };
        return new TextResourceContents
        {
            Uri = $"memory://note/{id}",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(payload, JsonOpts),
        };
    }

    private static async Task<IReadOnlyList<object>> LoadNotePayloadsAsync(
        IQueryable<Note> query,
        int limit,
        CancellationToken ct)
    {
        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit <= 0 ? 20 : limit, 1, 100))
            .Select(n => new
            {
                n.Id,
                n.Content,
                n.ContextDescription,
                n.Keywords,
                n.Tags,
                n.Kind,
                n.MemoryType,
                n.CreatedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(n => (object)new
        {
            id = n.Id.Value,
            content = n.Content,
            contextDescription = n.ContextDescription,
            keywords = n.Keywords,
            tags = n.Tags,
            kind = n.Kind.ToString(),
            memoryType = n.MemoryType.ToString(),
            createdAt = n.CreatedAt,
        }).ToArray();
    }

    [McpServerResource(UriTemplate = "memory://reflection/latest", Name = "Latest reflection")]
    [Description("Most recent reflection summary for the active project.")]
    public static async Task<TextResourceContents> GetLatestReflectionAsync(
        MemoryDbContext db,
        ITenantContext tenant,
        CancellationToken ct = default)
    {
        _ = tenant.Require();
        var reflection = await db.Reflections
            .OrderByDescending(r => r.GeneratedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (reflection is null)
        {
            return new TextResourceContents
            {
                Uri = "memory://reflection/latest",
                MimeType = "application/json",
                Text = "null",
            };
        }
        var payload = new
        {
            id = reflection.Id.Value,
            scope = reflection.Scope,
            generatedAt = reflection.GeneratedAt,
            generatorModel = reflection.GeneratorModel,
            summary = reflection.Summary,
        };
        return new TextResourceContents
        {
            Uri = "memory://reflection/latest",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(payload, JsonOpts),
        };
    }
}
