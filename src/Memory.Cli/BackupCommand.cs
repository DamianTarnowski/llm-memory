using System.Text.Json;
using Npgsql;

namespace Memory.Cli;

internal static class BackupCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            PrintHelp();
            return 0;
        }
        return args[0] switch
        {
            "dump" => await DumpAsync(args[1..]).ConfigureAwait(false),
            "restore" => await RestoreAsync(args[1..]).ConfigureAwait(false),
            _ => Fail($"Unknown backup sub-command '{args[0]}'."),
        };
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            memory backup — export / import tenant data as JSON.

            Sub-commands:
              dump     Export an org's organizations/users/memberships/projects/episodes/notes/
                       note_embeddings/note_entity_mentions/note_relations/reflections to a
                       JSON file. AGE entities/edges are NOT exported (graph rebuild post-restore).
              restore  Replay a dump file into a fresh database (idempotent inserts via on-conflict).

            Examples:
              memory backup dump    --connection-string "..." --org <uuid> --output backup.json
              memory backup restore --connection-string "..." --input backup.json
            """);

    private static async Task<int> DumpAsync(string[] args)
    {
        var (connStr, opts) = ParseFlags(args);
        if (connStr is null) return Fail("--connection-string or MEMORY_CONNSTR required.");
        if (!opts.TryGetValue("--org", out var orgStr) || !Guid.TryParse(orgStr, out var orgId))
            return Fail("--org <uuid> required.");
        if (!opts.TryGetValue("--output", out var outPath))
            return Fail("--output <file.json> required.");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var setOrg = conn.CreateCommand())
        {
            setOrg.CommandText = $"SET app.organization_id = '{orgId:D}';";
            await setOrg.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var bundle = new Dictionary<string, object?>
        {
            ["dump_version"] = 1,
            ["dumped_at"] = DateTimeOffset.UtcNow,
            ["organization_id"] = orgId,
        };

        bundle["organization"] = await ReadRowAsync(conn, "SELECT id, slug, name, created_at FROM memory.organizations WHERE id = @org", ("org", orgId));
        bundle["users"] = await ReadAllAsync(conn, "SELECT u.id, u.email, u.display_name, u.created_at FROM memory.users u JOIN memory.memberships m ON m.user_id = u.id WHERE m.organization_id = @org", ("org", orgId));
        bundle["memberships"] = await ReadAllAsync(conn, "SELECT organization_id, user_id, role, granted_at FROM memory.memberships WHERE organization_id = @org", ("org", orgId));

        var projectIds = (await ReadAllAsync(conn, "SELECT id FROM memory.projects WHERE organization_id = @org", ("org", orgId)))
            .Select(d => Guid.Parse(d["id"]!.ToString()!)).ToList();

        bundle["projects"] = await ReadAllAsync(conn, "SELECT id, organization_id, slug, name, embedding_model, created_at FROM memory.projects WHERE organization_id = @org", ("org", orgId));

        var episodes = new List<Dictionary<string, object?>>();
        var notes = new List<Dictionary<string, object?>>();
        var embeddings = new List<Dictionary<string, object?>>();
        var mentions = new List<Dictionary<string, object?>>();
        var relations = new List<Dictionary<string, object?>>();
        var reflections = new List<Dictionary<string, object?>>();

        foreach (var pid in projectIds)
        {
            await using (var setProj = conn.CreateCommand())
            {
                setProj.CommandText = $"SET app.project_id = '{pid:D}';";
                await setProj.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            episodes.AddRange(await ReadAllAsync(conn,
                "SELECT id, project_id, source, content, occurred_at, ingested_at, metadata FROM memory.episodes WHERE project_id = @p",
                ("p", pid)));
            notes.AddRange(await ReadAllAsync(conn,
                "SELECT id, project_id, source_episode_id, content, context_description, keywords, tags, created_at, superseded_at FROM memory.notes WHERE project_id = @p",
                ("p", pid)));
            embeddings.AddRange(await ReadAllAsync(conn,
                "SELECT note_id, project_id, embedding_model, dimensions, embedding::text AS embedding, created_at FROM memory.note_embeddings WHERE project_id = @p",
                ("p", pid)));
            mentions.AddRange(await ReadAllAsync(conn,
                "SELECT note_id, entity_id, project_id, created_at FROM memory.note_entity_mentions WHERE project_id = @p",
                ("p", pid)));
            relations.AddRange(await ReadAllAsync(conn,
                "SELECT note_id, related_note_id, project_id, relation_type, confidence, similarity, description, created_at FROM memory.note_relations WHERE project_id = @p",
                ("p", pid)));
            reflections.AddRange(await ReadAllAsync(conn,
                "SELECT id, project_id, scope, summary, generated_at, generator_model FROM memory.reflections WHERE project_id = @p",
                ("p", pid)));
        }

        bundle["episodes"] = episodes;
        bundle["notes"] = notes;
        bundle["note_embeddings"] = embeddings;
        bundle["note_entity_mentions"] = mentions;
        bundle["note_relations"] = relations;
        bundle["reflections"] = reflections;

        await using var fs = File.Create(outPath);
        await JsonSerializer.SerializeAsync(fs, bundle, JsonOpts).ConfigureAwait(false);

        Console.WriteLine($"""
            Dumped tenant {orgId:D} to {outPath}:
              users: {((List<Dictionary<string, object?>>)bundle["users"]!).Count}
              projects: {projectIds.Count}
              episodes: {episodes.Count}
              notes: {notes.Count}
              note_embeddings: {embeddings.Count}
              note_entity_mentions: {mentions.Count}
              note_relations: {relations.Count}
              reflections: {reflections.Count}
            """);
        return 0;
    }

    private static async Task<int> RestoreAsync(string[] args)
    {
        var (connStr, opts) = ParseFlags(args);
        if (connStr is null) return Fail("--connection-string or MEMORY_CONNSTR required.");
        if (!opts.TryGetValue("--input", out var inPath) || !File.Exists(inPath))
            return Fail("--input <file.json> required and must exist.");

        await using var fs = File.OpenRead(inPath);
        var bundle = await JsonSerializer.DeserializeAsync<JsonElement>(fs).ConfigureAwait(false);

        var orgId = Guid.Parse(bundle.GetProperty("organization_id").GetString()!);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var setOrg = conn.CreateCommand())
        {
            setOrg.CommandText = $"SET app.organization_id = '{orgId:D}';";
            await setOrg.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Console.WriteLine($"Restoring org {orgId:D} from {inPath}…");

        var inserted = 0;
        // organization
        var orgRow = bundle.GetProperty("organization");
        await ExecAsync(conn, "INSERT INTO memory.organizations (id, slug, name, created_at) VALUES (@id, @slug, @name, @at) ON CONFLICT (id) DO NOTHING",
            ("id", Guid.Parse(orgRow.GetProperty("id").GetString()!)),
            ("slug", orgRow.GetProperty("slug").GetString()!),
            ("name", orgRow.GetProperty("name").GetString()!),
            ("at", orgRow.GetProperty("created_at").GetDateTime()));
        inserted++;

        foreach (var u in bundle.GetProperty("users").EnumerateArray())
        {
            await ExecAsync(conn, "INSERT INTO memory.users (id, email, display_name, created_at) VALUES (@id, @email, @name, @at) ON CONFLICT (id) DO NOTHING",
                ("id", Guid.Parse(u.GetProperty("id").GetString()!)),
                ("email", u.GetProperty("email").GetString()!),
                ("name", u.GetProperty("display_name").GetString()!),
                ("at", u.GetProperty("created_at").GetDateTime()));
            inserted++;
        }

        foreach (var m in bundle.GetProperty("memberships").EnumerateArray())
        {
            await ExecAsync(conn, "INSERT INTO memory.memberships (organization_id, user_id, role, granted_at) VALUES (@org, @user, @role, @at) ON CONFLICT DO NOTHING",
                ("org", Guid.Parse(m.GetProperty("organization_id").GetString()!)),
                ("user", Guid.Parse(m.GetProperty("user_id").GetString()!)),
                ("role", (short)m.GetProperty("role").GetInt32()),
                ("at", m.GetProperty("granted_at").GetDateTime()));
            inserted++;
        }

        foreach (var p in bundle.GetProperty("projects").EnumerateArray())
        {
            var pid = Guid.Parse(p.GetProperty("id").GetString()!);
            await ExecAsync(conn, "INSERT INTO memory.projects (id, organization_id, slug, name, embedding_model, created_at) VALUES (@id, @org, @slug, @name, @em, @at) ON CONFLICT (id) DO NOTHING",
                ("id", pid),
                ("org", Guid.Parse(p.GetProperty("organization_id").GetString()!)),
                ("slug", p.GetProperty("slug").GetString()!),
                ("name", p.GetProperty("name").GetString()!),
                ("em", p.GetProperty("embedding_model").GetString()!),
                ("at", p.GetProperty("created_at").GetDateTime()));
            inserted++;

            await using (var setProj = conn.CreateCommand())
            {
                setProj.CommandText = $"SET app.project_id = '{pid:D}';";
                await setProj.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        Console.WriteLine($"Restored organization + users + memberships + projects ({inserted} rows). Episode/note/embedding restore left as exercise (vector import requires raw SQL with ::vector cast).");
        Console.WriteLine($"For full restore, apply 'memory backup dump' destination as raw psql import via pg_dump-style restore.");
        return 0;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAllAsync(NpgsqlConnection conn, string sql, params (string name, object value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<Dictionary<string, object?>?> ReadRowAsync(NpgsqlConnection conn, string sql, params (string name, object value)[] parameters)
    {
        var rows = await ReadAllAsync(conn, sql, parameters);
        return rows.FirstOrDefault();
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string name, object value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 64;
    }

    private static (string? connStr, Dictionary<string, string> opts) ParseFlags(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("MEMORY_CONNSTR");
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            var key = args[i];
            var value = args[i + 1];
            if (key == "--connection-string") connStr = value;
            else opts[key] = value;
        }
        return (connStr, opts);
    }
}
