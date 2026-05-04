using System.Data;
using System.Globalization;
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
        const string cypher = """
            MATCH (n:Entity {project_id: $project_id})
            WHERE $name IS NULL OR toLower(n.name) CONTAINS toLower($name)
            RETURN n.id AS id,
                   n.name AS name,
                   n.kind AS kind,
                   n.first_seen_at AS first_seen_at,
                   n.last_seen_at AS last_seen_at,
                   n.attributes AS attributes
            LIMIT $limit
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["project_id"] = project.Value.ToString("D"),
            ["name"] = nameFilter,
            ["limit"] = limit,
        };

        var rows = await QueryAsync(cypher, parameters, columnCount: 6, ct).ConfigureAwait(false);

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
        const string cypher = """
            MATCH (a:Entity {project_id: $project_id})-[r:Edge {project_id: $project_id}]->(b:Entity {project_id: $project_id})
            WHERE ($from IS NULL OR a.id = $from)
              AND ($to IS NULL OR b.id = $to)
              AND ($relation IS NULL OR r.relation = $relation)
              AND ($valid_at IS NULL OR (
                  (r.valid_from IS NULL OR r.valid_from <= $valid_at)
                  AND (r.valid_to IS NULL OR r.valid_to > $valid_at)
                  AND r.invalidated_at IS NULL
              ))
            RETURN r.id AS id,
                   a.id AS from_id,
                   b.id AS to_id,
                   r.relation AS relation,
                   r.recorded_at AS recorded_at,
                   r.valid_from AS valid_from,
                   r.valid_to AS valid_to,
                   r.invalidated_at AS invalidated_at,
                   r.source_episode AS source_episode,
                   r.properties AS properties
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["project_id"] = project.Value.ToString("D"),
            ["from"] = from?.Value.ToString("D"),
            ["to"] = to?.Value.ToString("D"),
            ["relation"] = relation,
            ["valid_at"] = validAt?.ToString("o", CultureInfo.InvariantCulture),
        };

        var rows = await QueryAsync(cypher, parameters, columnCount: 10, ct).ConfigureAwait(false);

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
        const string cypher = """
            MERGE (n:Entity {project_id: $project_id, name: $name})
            ON CREATE SET n.id = $new_id, n.first_seen_at = $now
            SET n.last_seen_at = $now,
                n.kind = $kind,
                n.attributes = $attributes
            RETURN n.id
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["project_id"] = project.Value.ToString("D"),
            ["name"] = name,
            ["new_id"] = Guid.NewGuid().ToString("D"),
            ["kind"] = kind,
            ["attributes"] = attributes,
            ["now"] = seenAt.ToString("o", CultureInfo.InvariantCulture),
        };

        var raw = await ExecuteScalarAsync(cypher, parameters, ct).ConfigureAwait(false);
        return new EntityId(ReadGuid(raw));
    }

    public async Task AddEdgeAsync(Edge edge, CancellationToken ct = default)
    {
        const string cypher = """
            MATCH (a:Entity {id: $from_id, project_id: $project_id})
            MATCH (b:Entity {id: $to_id, project_id: $project_id})
            CREATE (a)-[r:Edge {
                id: $edge_id,
                project_id: $project_id,
                relation: $relation,
                recorded_at: $recorded_at,
                valid_from: $valid_from,
                valid_to: $valid_to,
                invalidated_at: null,
                source_episode: $source_episode,
                properties: $properties
            }]->(b)
            RETURN r.id
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["edge_id"] = edge.Id.Value.ToString("D"),
            ["project_id"] = edge.Project.Value.ToString("D"),
            ["from_id"] = edge.From.Value.ToString("D"),
            ["to_id"] = edge.To.Value.ToString("D"),
            ["relation"] = edge.Relation,
            ["recorded_at"] = edge.RecordedAt.ToString("o", CultureInfo.InvariantCulture),
            ["valid_from"] = edge.ValidFrom?.ToString("o", CultureInfo.InvariantCulture),
            ["valid_to"] = edge.ValidTo?.ToString("o", CultureInfo.InvariantCulture),
            ["source_episode"] = edge.SourceEpisode?.Value.ToString("D"),
            ["properties"] = edge.Properties,
        };

        await ExecuteAsync(cypher, parameters, returnColumn: "id", ct).ConfigureAwait(false);
    }

    public async Task InvalidateEdgeAsync(EdgeId id, DateTimeOffset at, CancellationToken ct = default)
    {
        const string cypher = """
            MATCH ()-[r:Edge {id: $edge_id}]->()
            SET r.invalidated_at = $at
            RETURN r.id
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["edge_id"] = id.Value.ToString("D"),
            ["at"] = at.ToString("o", CultureInfo.InvariantCulture),
        };

        await ExecuteAsync(cypher, parameters, returnColumn: "id", ct).ConfigureAwait(false);
    }

    private async Task<List<string?[]>> QueryAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        int columnCount,
        CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = BuildSql(cypher, hasParams: parameters.Count > 0, columnCount);

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (parameters.Count > 0)
        {
            cmd.Parameters.AddWithValue("params", JsonSerializer.Serialize(parameters, AgtypeParser.JsonOpts.Web));
        }

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

    private async Task ExecuteAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        string returnColumn,
        CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = BuildSql(cypher, hasParams: parameters.Count > 0, columnCount: 1);

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (parameters.Count > 0)
        {
            cmd.Parameters.AddWithValue("params", JsonSerializer.Serialize(parameters, AgtypeParser.JsonOpts.Web));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            _ = reader.GetValue(0);
        }
    }

    private async Task<string> ExecuteScalarAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = BuildSql(cypher, hasParams: parameters.Count > 0, columnCount: 1);

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (parameters.Count > 0)
        {
            cmd.Parameters.AddWithValue("params", JsonSerializer.Serialize(parameters, AgtypeParser.JsonOpts.Web));
        }

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

    private string BuildSql(string cypher, bool hasParams, int columnCount)
    {
        var columns = string.Join(", ", Enumerable.Range(0, columnCount).Select(i => $"col{i} agtype"));
        return hasParams
            ? $"SELECT * FROM ag_catalog.cypher('{_options.GraphName}', $cy${cypher}$cy$, @params::jsonb) AS ({columns});"
            : $"SELECT * FROM ag_catalog.cypher('{_options.GraphName}', $cy${cypher}$cy$) AS ({columns});";
    }

    private static Guid ReadGuid(string? raw)
    {
        if (raw is null) throw new InvalidOperationException("Expected non-null Guid agtype value.");
        var s = TrimAgtypeString(raw);
        return Guid.Parse(s, CultureInfo.InvariantCulture);
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
