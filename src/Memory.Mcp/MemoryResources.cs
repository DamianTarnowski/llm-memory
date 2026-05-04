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
