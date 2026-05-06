using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Memory.Api;

/// <summary>
/// Tenant-scoped full-corpus dump as a single .zip the user can download. The
/// zip's contents are intentionally human-browsable: per-entity JSON files plus
/// a <c>notes-md/</c> folder of one Markdown per active note (Obsidian-friendly
/// frontmatter). Useful for backup, audit, portability, and the simple
/// reassurance that "I can get my data out of this thing if I need to."
/// </summary>
public static class BackupEndpoints
{
    public static void MapBackup(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/backup/download", DownloadAsync);
    }

    private static async Task<IResult> DownloadAsync(
        MemoryDbContext db,
        IGraphContext graph,
        ITenantContext tenant,
        bool? includeEmbeddings,
        bool? includeImageEmbeddings,
        CancellationToken ct)
    {
        var scope = tenant.Require();
        var withTextEmbeddings = includeEmbeddings ?? true;
        var withImageEmbeddings = includeImageEmbeddings ?? true;

        // Load everything for the tenant. Order chosen so dependent entities (notes
        // need episodes, mentions need notes + entities) are present together when
        // someone reads the zip top-to-bottom.
        var episodes = await db.Episodes
            .Where(e => e.Project == scope.Project)
            .OrderBy(e => e.IngestedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var notes = await db.Notes
            .Where(n => n.Project == scope.Project)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var noteEmbeddings = withTextEmbeddings
            ? await db.NoteEmbeddings.Where(e => e.Project == scope.Project).ToListAsync(ct).ConfigureAwait(false)
            : new List<NoteEmbedding>();
        var noteEntityMentions = await db.NoteEntityMentions
            .Where(m => m.Project == scope.Project)
            .ToListAsync(ct).ConfigureAwait(false);
        var noteRelations = await db.NoteRelations
            .Where(r => r.Project == scope.Project)
            .ToListAsync(ct).ConfigureAwait(false);
        var reflections = await db.Reflections
            .Where(r => r.Project == scope.Project)
            .OrderBy(r => r.GeneratedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var imageEmbeddings = withImageEmbeddings
            ? await db.ImageEmbeddings.Where(e => e.Project == scope.Project).ToListAsync(ct).ConfigureAwait(false)
            : new List<ImageEmbedding>();
        var entities = await graph.GetEntitiesAsync(scope.Project, limit: 10_000, ct: ct).ConfigureAwait(false);
        var edges = await graph.GetEdgesAsync(scope.Project, ct: ct).ConfigureAwait(false);

        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteJsonAsync(archive, "manifest.json", new
            {
                schemaVersion = 1,
                generatedAt = DateTimeOffset.UtcNow,
                project = scope.Project.Value,
                organization = scope.Organization.Value,
                user = scope.User.Value,
                counts = new
                {
                    episodes = episodes.Count,
                    notes = notes.Count,
                    activeNotes = notes.Count(n => n.SupersededAt is null),
                    noteEmbeddings = noteEmbeddings.Count,
                    noteEntityMentions = noteEntityMentions.Count,
                    noteRelations = noteRelations.Count,
                    reflections = reflections.Count,
                    imageEmbeddings = imageEmbeddings.Count,
                    entities = entities.Count,
                    edges = edges.Count,
                    activeEdges = edges.Count(e => e.InvalidatedAt is null),
                },
                options = new { withTextEmbeddings, withImageEmbeddings },
                note = "Restore via 'memory backup restore --in <unzipped>/...'. Note embeddings are tightly coupled to the embedding model that produced them; restoring into a project with a different model is a re-ingest, not a copy.",
            }, jsonOpts, ct).ConfigureAwait(false);

            await WriteJsonAsync(archive, "episodes.json", episodes.Select(e => new
            {
                id = e.Id.Value,
                source = e.Source,
                content = e.Content,
                occurredAt = e.OccurredAt,
                ingestedAt = e.IngestedAt,
                metadata = e.Metadata,
            }), jsonOpts, ct).ConfigureAwait(false);

            await WriteJsonAsync(archive, "notes.json", notes.Select(n => new
            {
                id = n.Id.Value,
                sourceEpisode = n.SourceEpisode?.Value,
                content = n.Content,
                contextDescription = n.ContextDescription,
                keywords = n.Keywords,
                tags = n.Tags,
                kind = n.Kind.ToString(),
                createdAt = n.CreatedAt,
                supersededAt = n.SupersededAt,
            }), jsonOpts, ct).ConfigureAwait(false);

            if (withTextEmbeddings)
            {
                await WriteJsonAsync(archive, "note_embeddings.json", noteEmbeddings.Select(e => new
                {
                    noteId = e.NoteId.Value,
                    model = e.EmbeddingModel,
                    dimensions = e.Dimensions,
                    embedding = e.Embedding,
                    createdAt = e.CreatedAt,
                }), jsonOpts, ct).ConfigureAwait(false);
            }

            await WriteJsonAsync(archive, "note_entity_mentions.json", noteEntityMentions.Select(m => new
            {
                noteId = m.NoteId.Value,
                entityId = m.EntityId.Value,
                createdAt = m.CreatedAt,
            }), jsonOpts, ct).ConfigureAwait(false);

            await WriteJsonAsync(archive, "note_relations.json", noteRelations.Select(r => new
            {
                noteId = r.NoteId.Value,
                relatedNoteId = r.RelatedNoteId.Value,
                relationType = r.RelationType,
                confidence = r.Confidence,
                similarity = r.Similarity,
                description = r.Description,
                createdAt = r.CreatedAt,
            }), jsonOpts, ct).ConfigureAwait(false);

            await WriteJsonAsync(archive, "reflections.json", reflections.Select(r => new
            {
                id = r.Id.Value,
                scope = r.Scope,
                summary = r.Summary,
                generatedAt = r.GeneratedAt,
                generatorModel = r.GeneratorModel,
            }), jsonOpts, ct).ConfigureAwait(false);

            if (withImageEmbeddings && imageEmbeddings.Count > 0)
            {
                await WriteJsonAsync(archive, "image_embeddings.json", imageEmbeddings.Select(e => new
                {
                    id = e.Id,
                    noteId = e.NoteId.Value,
                    model = e.ModelId,
                    dimensions = e.Dimensions,
                    embedding = e.Embedding,
                    createdAt = e.CreatedAt,
                }), jsonOpts, ct).ConfigureAwait(false);
            }

            await WriteJsonAsync(archive, "entities.json", entities.Select(e => new
            {
                id = e.Id.Value,
                name = e.Name,
                kind = e.Kind,
                attributes = e.Attributes,
                firstSeenAt = e.FirstSeenAt,
                lastSeenAt = e.LastSeenAt,
            }), jsonOpts, ct).ConfigureAwait(false);

            await WriteJsonAsync(archive, "edges.json", edges.Select(e => new
            {
                id = e.Id.Value,
                from = e.From.Value,
                to = e.To.Value,
                relation = e.Relation,
                properties = e.Properties,
                recordedAt = e.RecordedAt,
                validFrom = e.ValidFrom,
                validTo = e.ValidTo,
                invalidatedAt = e.InvalidatedAt,
                sourceEpisode = e.SourceEpisode?.Value,
            }), jsonOpts, ct).ConfigureAwait(false);

            // Markdown bundle — one .md per active note, Obsidian-friendly. Skipped
            // for superseded notes since they're history, not the current view.
            foreach (var n in notes.Where(n => n.SupersededAt is null))
            {
                var sb = new StringBuilder();
                sb.AppendLine("---");
                sb.AppendLine($"id: {n.Id.Value}");
                sb.AppendLine($"kind: {n.Kind}");
                sb.AppendLine($"created: {n.CreatedAt:O}");
                if (n.Tags.Count > 0) sb.AppendLine($"tags: [{string.Join(", ", n.Tags)}]");
                if (n.Keywords.Count > 0) sb.AppendLine($"keywords: [{string.Join(", ", n.Keywords)}]");
                if (!string.IsNullOrEmpty(n.ContextDescription)) sb.AppendLine($"context: {YamlEscape(n.ContextDescription)}");
                sb.AppendLine("---");
                sb.AppendLine();
                sb.Append(n.Content);

                var slug = Slugify(n.Content);
                var path = $"notes-md/{n.CreatedAt:yyyy-MM-dd}_{n.Kind.ToString().ToLowerInvariant()}_{slug}_{n.Id.Value.ToString("N")[..8]}.md";
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(sb.ToString()).ConfigureAwait(false);
            }
        }
        ms.Position = 0;
        var bytes = ms.ToArray();

        var filename = $"memory-backup-{scope.Project.Value}-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.zip";
        return Results.File(bytes, "application/zip", filename);
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T payload, JsonSerializerOptions opts, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, payload, opts, ct).ConfigureAwait(false);
    }

    private static string Slugify(string content)
    {
        var first40 = content.Length > 40 ? content[..40] : content;
        var sb = new StringBuilder();
        foreach (var c in first40.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "note";
    }

    private static string YamlEscape(string value) =>
        value.Contains(':') || value.Contains('#') || value.Contains('\n')
            ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : value;
}
