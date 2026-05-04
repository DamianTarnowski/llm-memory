using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.Storage.Age;

internal sealed class AgeGraphContext(MemoryDbContext db, IOptions<StorageOptions> options) : IGraphContext
{
    private readonly StorageOptions _options = options.Value;

    public async Task<IReadOnlyList<Entity>> GetEntitiesAsync(
        ProjectId project,
        string? nameFilter = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var nameClause = nameFilter is null
            ? string.Empty
            : $"WHERE toLower(n.name) CONTAINS toLower({CypherStr(nameFilter)})";

        var cypher = $$"""
            MATCH (n:Entity {project_id: {{CypherStr(project.Value.ToString("D"))}}})
            {{nameClause}}
            RETURN n.id, n.name, n.kind, n.first_seen_at, n.last_seen_at, n.attributes
            LIMIT {{limit}}
            """;

        var rows = await QueryAsync(cypher, columnCount: 6, ct).ConfigureAwait(false);

        var entities = new List<Entity>(rows.Count);
        foreach (var row in rows)
        {
            entities.Add(new Entity
            {
                Id = new EntityId(ReadGuid(row[0])),
                Project = project,
                Name = ReadString(row[1]),
                Kind = ReadString(row[2]),
                FirstSeenAt = ReadDateTimeOffset(row[3]),
                LastSeenAt = ReadDateTimeOffset(row[4]),
                Attributes = ReadStringDictionary(row[5]),
            });
        }
        return entities;
    }

    public async Task<IReadOnlyList<Edge>> GetEdgesAsync(
        ProjectId project,
        EntityId? from = null,
        EntityId? to = null,
        string? relation = null,
        DateTimeOffset? validAt = null,
        CancellationToken ct = default)
    {
        var conditions = new List<string>();
        if (from is { } f) conditions.Add($"a.id = {CypherStr(f.Value.ToString("D"))}");
        if (to is { } t) conditions.Add($"b.id = {CypherStr(t.Value.ToString("D"))}");
        if (relation is not null) conditions.Add($"r.relation = {CypherStr(relation)}");
        if (validAt is { } va)
        {
            var ts = CypherStr(va.ToString("o", CultureInfo.InvariantCulture));
            conditions.Add($"(r.valid_from IS NULL OR r.valid_from <= {ts})");
            conditions.Add($"(r.valid_to IS NULL OR r.valid_to > {ts})");
            conditions.Add("r.invalidated_at IS NULL");
        }
        var whereClause = conditions.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", conditions);

        var pid = CypherStr(project.Value.ToString("D"));
        var cypher = $$"""
            MATCH (a:Entity {project_id: {{pid}}})-[r:Edge {project_id: {{pid}}}]->(b:Entity {project_id: {{pid}}})
            {{whereClause}}
            RETURN r.id, a.id, b.id, r.relation, r.recorded_at, r.valid_from, r.valid_to, r.invalidated_at, r.source_episode, r.properties
            """;

        var rows = await QueryAsync(cypher, columnCount: 10, ct).ConfigureAwait(false);

        var edges = new List<Edge>(rows.Count);
        foreach (var row in rows)
        {
            edges.Add(new Edge
            {
                Id = new EdgeId(ReadGuid(row[0])),
                Project = project,
                From = new EntityId(ReadGuid(row[1])),
                To = new EntityId(ReadGuid(row[2])),
                Relation = ReadString(row[3]),
                RecordedAt = ReadDateTimeOffset(row[4]),
                ValidFrom = ReadOptionalDateTimeOffset(row[5]),
                ValidTo = ReadOptionalDateTimeOffset(row[6]),
                InvalidatedAt = ReadOptionalDateTimeOffset(row[7]),
                SourceEpisode = ReadOptionalEpisodeId(row[8]),
                Properties = ReadStringDictionary(row[9]),
            });
        }
        return edges;
    }

    public async Task<EntityId> UpsertEntityAsync(
        ProjectId project,
        string name,
        string kind,
        IReadOnlyDictionary<string, string> attributes,
        DateTimeOffset seenAt,
        CancellationToken ct = default)
    {
        var newId = Guid.NewGuid().ToString("D");
        var now = seenAt.ToString("o", CultureInfo.InvariantCulture);
        // AGE 1.6 does not support ON CREATE SET / ON MATCH SET — emulate with COALESCE
        // so id and first_seen_at are preserved on existing nodes.
        var cypher = $$"""
            MERGE (n:Entity {project_id: {{CypherStr(project.Value.ToString("D"))}}, name: {{CypherStr(name)}}})
            SET n.id = COALESCE(n.id, {{CypherStr(newId)}}),
                n.first_seen_at = COALESCE(n.first_seen_at, {{CypherStr(now)}}),
                n.last_seen_at = {{CypherStr(now)}},
                n.kind = {{CypherStr(kind)}},
                n.attributes = {{CypherMap(attributes)}}
            RETURN n.id
            """;

        var raw = await ExecuteScalarAsync(cypher, ct).ConfigureAwait(false);
        return new EntityId(ReadGuid(raw));
    }

    public async Task AddEdgeAsync(Edge edge, CancellationToken ct = default)
    {
        var pid = CypherStr(edge.Project.Value.ToString("D"));
        var rec = CypherStr(edge.RecordedAt.ToString("o", CultureInfo.InvariantCulture));
        // MERGE (vs CREATE) so repeated saves of the same (from, to, relation) edge in
        // a project don't accumulate duplicates. AGE 1.6 has no ON CREATE SET, so we
        // emulate it with COALESCE — original metadata is preserved on existing edges.
        var cypher = $$"""
            MATCH (a:Entity {id: {{CypherStr(edge.From.Value.ToString("D"))}}, project_id: {{pid}}})
            MATCH (b:Entity {id: {{CypherStr(edge.To.Value.ToString("D"))}}, project_id: {{pid}}})
            MERGE (a)-[r:Edge {project_id: {{pid}}, relation: {{CypherStr(edge.Relation)}}}]->(b)
            SET r.id = COALESCE(r.id, {{CypherStr(edge.Id.Value.ToString("D"))}}),
                r.recorded_at = COALESCE(r.recorded_at, {{rec}}),
                r.valid_from = COALESCE(r.valid_from, {{CypherOptStr(edge.ValidFrom?.ToString("o", CultureInfo.InvariantCulture))}}),
                r.valid_to = {{CypherOptStr(edge.ValidTo?.ToString("o", CultureInfo.InvariantCulture))}},
                r.source_episode = COALESCE(r.source_episode, {{CypherOptStr(edge.SourceEpisode?.Value.ToString("D"))}}),
                r.properties = {{CypherMap(edge.Properties)}}
            RETURN r.id
            """;

        await ExecuteAsync(cypher, ct).ConfigureAwait(false);
    }

    public async Task InvalidateEdgeAsync(EdgeId id, DateTimeOffset at, CancellationToken ct = default)
    {
        var cypher = $$"""
            MATCH ()-[r:Edge {id: {{CypherStr(id.Value.ToString("D"))}}}]->()
            SET r.invalidated_at = {{CypherStr(at.ToString("o", CultureInfo.InvariantCulture))}}
            RETURN r.id
            """;

        await ExecuteAsync(cypher, ct).ConfigureAwait(false);
    }

    private async Task<List<string?[]>> QueryAsync(string cypher, int columnCount, CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = BuildSql(cypher, columnCount);

        await using var cmd = new NpgsqlCommand(sql, conn);
        var rows = new List<string?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new string?[columnCount];
            for (var i = 0; i < columnCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    private async Task ExecuteAsync(string cypher, CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = BuildSql(cypher, columnCount: 1);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            _ = reader.GetValue(0);
        }
    }

    private async Task<string> ExecuteScalarAsync(string cypher, CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = BuildSql(cypher, columnCount: 1);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Cypher query did not return any rows.");
        }
        return reader.IsDBNull(0)
            ? throw new InvalidOperationException("Cypher query returned NULL where a value was expected.")
            : reader.GetString(0);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        return (NpgsqlConnection)db.Database.GetDbConnection();
    }

    private string BuildSql(string cypher, int columnCount)
    {
        // AGE requires `LOAD 'age'` and ag_catalog on search_path each session for its
        // operator overloads (e.g. @> for MERGE) to resolve. Both are idempotent.
        // Cast agtype results to text so Npgsql can read them as strings.
        var declared = string.Join(", ", Enumerable.Range(0, columnCount).Select(i => $"col{i} ag_catalog.agtype"));
        var projected = string.Join(", ", Enumerable.Range(0, columnCount).Select(i => $"col{i}::text"));
        return $"""
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            SELECT {projected} FROM ag_catalog.cypher('{_options.GraphName}', $cy${cypher}$cy$) AS ({declared});
            """;
    }

    private static string CypherStr(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'': sb.Append("\\'"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    private static string CypherOptStr(string? value) => value is null ? "null" : CypherStr(value);

    private static string CypherMap(IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count == 0) return "{}";
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kv in attributes)
        {
            if (!first) sb.Append(", ");
            sb.Append(EscapeMapKey(kv.Key)).Append(": ").Append(CypherStr(kv.Value));
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeMapKey(string key)
    {
        // Cypher map keys are identifiers; if the key isn't a valid identifier wrap in backticks.
        if (key.Length == 0) return "``";
        var clean = true;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (i == 0 ? !char.IsLetter(c) && c != '_' : !char.IsLetterOrDigit(c) && c != '_')
            {
                clean = false;
                break;
            }
        }
        return clean ? key : "`" + key.Replace("`", "``") + "`";
    }

    private static Guid ReadGuid(string? raw)
    {
        if (raw is null) throw new InvalidOperationException("Expected non-null Guid agtype value.");
        return Guid.Parse(TrimAgtypeString(raw), CultureInfo.InvariantCulture);
    }

    private static string ReadString(string? raw)
    {
        if (raw is null) throw new InvalidOperationException("Expected non-null string agtype value.");
        return TrimAgtypeString(raw);
    }

    private static DateTimeOffset ReadDateTimeOffset(string? raw)
    {
        if (raw is null) throw new InvalidOperationException("Expected non-null timestamp agtype value.");
        return DateTimeOffset.Parse(TrimAgtypeString(raw), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(string? raw)
    {
        if (raw is null || raw == "null") return null;
        return DateTimeOffset.Parse(TrimAgtypeString(raw), CultureInfo.InvariantCulture);
    }

    private static EpisodeId? ReadOptionalEpisodeId(string? raw)
    {
        if (raw is null || raw == "null") return null;
        return new EpisodeId(Guid.Parse(TrimAgtypeString(raw), CultureInfo.InvariantCulture));
    }

    private static Dictionary<string, string> ReadStringDictionary(string? raw)
    {
        if (raw is null || raw == "null") return new Dictionary<string, string>();
        var element = AgtypeParser.Parse(raw);
        if (element.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => prop.Value.GetRawText(),
            };
        }
        return result;
    }

    private static string TrimAgtypeString(string raw)
    {
        var span = raw.AsSpan().Trim();
        if (span.Length >= 2 && span[0] == '"' && span[^1] == '"')
        {
            return span[1..^1].ToString();
        }
        return span.ToString();
    }
}
