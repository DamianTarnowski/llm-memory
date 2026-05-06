using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Memory.Cli;

internal static class ApiKeyCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            PrintHelp();
            return 0;
        }

        return args[0] switch
        {
            "create" => await CreateAsync(args[1..]).ConfigureAwait(false),
            "list" => await ListAsync(args[1..]).ConfigureAwait(false),
            "revoke" => await RevokeAsync(args[1..]).ConfigureAwait(false),
            _ =>
                Fail($"Unknown api-key sub-command '{args[0]}'. See 'memory api-key help'."),
        };
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            memory api-key — manage tenant API keys

            Sub-commands:
              create    Generate a new API key bound to an org/user/project.
              list      List active keys (hashes + names + project ids).
              revoke    Revoke a key by id.

            Examples:
              memory api-key create --connection-string "..." \
                --org <org-uuid> --user <user-uuid> --project <project-uuid> \
                --name "claude-desktop"

              memory api-key create ... --admin   # adds /api/secrets/* admin scope
              memory api-key list   --connection-string "..."
              memory api-key revoke --connection-string "..." --id <api-key-id>
            """);

    private static async Task<int> CreateAsync(string[] args)
    {
        var (connStr, opts) = ParseFlags(args);
        if (connStr is null) return Fail("Connection string required (--connection-string or MEMORY_CONNSTR).");
        if (!opts.TryGetValue("--org", out var orgStr) || !Guid.TryParse(orgStr, out var orgId))
            return Fail("--org <uuid> is required.");
        if (!opts.TryGetValue("--user", out var userStr) || !Guid.TryParse(userStr, out var userId))
            return Fail("--user <uuid> is required.");
        if (!opts.TryGetValue("--project", out var projStr) || !Guid.TryParse(projStr, out var projectId))
            return Fail("--project <uuid> is required.");
        opts.TryGetValue("--name", out var name);
        name ??= "unnamed";
        var isAdmin = args.Contains("--admin", StringComparer.OrdinalIgnoreCase);

        var rawKey = GenerateKey();
        var hash = HashToken(rawKey);
        var id = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.organization_id = '{orgId:D}'; SET app.project_id = '{projectId:D}';";
            await setCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO memory.api_keys
              (id, key_hash, organization_id, project_id, created_by_user_id, name, created_at, is_admin)
            VALUES (@id, @hash, @org, @proj, @user, @name, @now, @is_admin);
            """;
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("org", orgId);
        insert.Parameters.AddWithValue("proj", projectId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("name", name);
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        insert.Parameters.AddWithValue("is_admin", isAdmin);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);

        var adminBanner = isAdmin
            ? "\n            !! ADMIN KEY — can call /api/secrets/* (OpenBao proxy). Treat carefully.\n"
            : "";
        Console.WriteLine($"""
            API key created (id {id:D}). Save this token now — it will NOT be shown again:

              {rawKey}
            {adminBanner}
            Send as `Authorization: Bearer <token>` to Memory.Api endpoints.
            """);
        return 0;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        var (connStr, _) = ParseFlags(args);
        if (connStr is null) return Fail("Connection string required (--connection-string or MEMORY_CONNSTR).");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, organization_id, project_id, created_at, last_used_at, revoked_at, is_admin
            FROM memory.api_keys
            ORDER BY created_at DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        Console.WriteLine($"{"id",-38} {"name",-25} {"project",-38} {"role",-7} {"created",-30} {"revoked"}");
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var name = reader.GetString(1);
            var proj = reader.GetGuid(3);
            var created = reader.GetDateTime(4);
            var revoked = reader.IsDBNull(6) ? "" : reader.GetDateTime(6).ToString("u");
            var role = reader.GetBoolean(7) ? "admin" : "tenant";
            Console.WriteLine($"{id:D} {name,-25} {proj:D} {role,-7} {created:u} {revoked}");
        }
        return 0;
    }

    private static async Task<int> RevokeAsync(string[] args)
    {
        var (connStr, opts) = ParseFlags(args);
        if (connStr is null) return Fail("Connection string required (--connection-string or MEMORY_CONNSTR).");
        if (!opts.TryGetValue("--id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return Fail("--id <uuid> is required.");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memory.api_keys SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("id", id);
        var rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        Console.WriteLine(rows == 0 ? "No active key with that id." : $"Revoked key {id:D}.");
        return 0;
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

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "memk_" + Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
