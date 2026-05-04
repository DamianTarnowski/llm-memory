using Npgsql;

namespace Memory.Cli;

/// <summary>
/// Foundation commands for the schema-per-org tenancy mode. The mapping table
/// (<c>memory.tenant_schemas</c>) is populated here, but Memory.Api and Memory.Mcp
/// connections still target the central <c>memory</c> schema with RLS — connection
/// routing is intentionally not yet wired. Provisioning a schema is therefore a
/// no-op operationally; it exists so the migration path can be taken incrementally.
/// </summary>
internal static class TenantsCommand
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
            "provision-schema" => await ProvisionAsync(args[1..]).ConfigureAwait(false),
            "list" => await ListAsync(args[1..]).ConfigureAwait(false),
            "drop-schema" => await DropAsync(args[1..]).ConfigureAwait(false),
            _ => Fail($"Unknown tenants sub-command '{args[0]}'. See 'memory tenants help'."),
        };
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            memory tenants — schema-per-org tenancy foundation

            Sub-commands:
              provision-schema  Create an empty postgres schema for an organization and register it in
                                memory.tenant_schemas (status: provisioned). Connection routing is NOT
                                yet wired — Memory.Api and the MCP servers still serve from the central
                                memory schema with RLS. Once routing exists, flipping the schema's
                                status to active will switch the org's reads/writes to it.
              list              List registered tenant schemas.
              drop-schema       Drop a schema (and the registry row). Refuses if status = active.

            Examples:
              memory tenants provision-schema --connection-string "..." --org <org-uuid>
              memory tenants list --connection-string "..."
              memory tenants drop-schema --connection-string "..." --org <org-uuid>
            """);

    private static async Task<int> ProvisionAsync(string[] args)
    {
        var (connStr, opts) = ParseFlags(args);
        if (connStr is null) return Fail("Connection string required (--connection-string or MEMORY_CONNSTR).");
        if (!opts.TryGetValue("--org", out var orgStr) || !Guid.TryParse(orgStr, out var orgId))
            return Fail("--org <uuid> is required.");

        // Schema name = "org_" + first 12 hex chars of the org id, no dashes. Stays under
        // postgres' 63-char identifier limit and stays human-recognizable.
        var schemaName = "org_" + orgId.ToString("N")[..12];

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        // Idempotency guard.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT schema_name, status FROM memory.tenant_schemas WHERE organization_id = @org;";
            check.Parameters.AddWithValue("org", orgId);
            await using var reader = await check.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                Console.WriteLine($"Org {orgId:D} already has schema '{reader.GetString(0)}' (status code {reader.GetInt16(1)}). Nothing to do.");
                return 0;
            }
        }

        // CREATE SCHEMA + INSERT in one transaction. We don't clone the table DDL here —
        // table cloning happens at activation time once routing is wired.
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        await using (var createSchema = conn.CreateCommand())
        {
            createSchema.Transaction = tx;
            createSchema.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\";";
            await createSchema.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO memory.tenant_schemas
                  (organization_id, schema_name, status, created_at)
                VALUES (@org, @schema, 0, @now);
                """;
            insert.Parameters.AddWithValue("org", orgId);
            insert.Parameters.AddWithValue("schema", schemaName);
            insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);

        Console.WriteLine($"""
            Schema '{schemaName}' provisioned for org {orgId:D}.
            Status: provisioned (RLS still active for this org's actual data).
            Next step (when ready to flip): clone tables into the schema, set status = active,
              then update the connection interceptor to set search_path per request.
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
            SELECT organization_id, schema_name, status, created_at, activated_at
            FROM memory.tenant_schemas
            ORDER BY created_at DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

        Console.WriteLine($"{"organization",-38} {"schema",-25} {"status",-13} {"created"}");
        var any = false;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            any = true;
            var org = reader.GetGuid(0);
            var schema = reader.GetString(1);
            var status = (short)reader.GetValue(2) switch
            {
                0 => "provisioned",
                1 => "active",
                2 => "deactivated",
                _ => $"?({reader.GetValue(2)})",
            };
            var created = reader.GetDateTime(3);
            Console.WriteLine($"{org:D} {schema,-25} {status,-13} {created:u}");
        }
        if (!any) Console.WriteLine("(no tenant schemas registered)");
        return 0;
    }

    private static async Task<int> DropAsync(string[] args)
    {
        var (connStr, opts) = ParseFlags(args);
        if (connStr is null) return Fail("Connection string required (--connection-string or MEMORY_CONNSTR).");
        if (!opts.TryGetValue("--org", out var orgStr) || !Guid.TryParse(orgStr, out var orgId))
            return Fail("--org <uuid> is required.");
        var force = opts.ContainsKey("--force");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync().ConfigureAwait(false);

        string schemaName;
        short status;
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT schema_name, status FROM memory.tenant_schemas WHERE organization_id = @org;";
            check.Parameters.AddWithValue("org", orgId);
            await using var reader = await check.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                Console.WriteLine($"No tenant schema registered for org {orgId:D}.");
                return 0;
            }
            schemaName = reader.GetString(0);
            status = reader.GetInt16(1);
        }

        if (status == 1 && !force)
        {
            return Fail($"Schema '{schemaName}' is currently ACTIVE. Refusing to drop without --force.");
        }

        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        await using (var dropSchema = conn.CreateCommand())
        {
            dropSchema.Transaction = tx;
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;";
            await dropSchema.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM memory.tenant_schemas WHERE organization_id = @org;";
            del.Parameters.AddWithValue("org", orgId);
            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);
        Console.WriteLine($"Dropped schema '{schemaName}' and de-registered org {orgId:D}.");
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
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--force")
            {
                opts[key] = "true";
                continue;
            }
            if (i + 1 >= args.Length) break;
            var value = args[i + 1];
            if (key == "--connection-string") connStr = value;
            else opts[key] = value;
            i++;
        }
        return (connStr, opts);
    }
}
