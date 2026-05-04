using System.Text.Json;
using Npgsql;

namespace Memory.Cli;

internal static class InitCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseArgs(args);

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var conn = new NpgsqlConnection(opts.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.organization_id = '{orgId:D}'; SET app.project_id = '{projectId:D}';";
            await setCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO memory.organizations (id, slug, name, created_at)
                VALUES (@org_id, @org_slug, @org_name, @now);

                INSERT INTO memory.users (id, email, display_name, created_at)
                VALUES (@user_id, @user_email, @user_name, @now);

                INSERT INTO memory.memberships (organization_id, user_id, role, granted_at)
                VALUES (@org_id, @user_id, 0, @now);

                INSERT INTO memory.projects (id, organization_id, slug, name, embedding_model, created_at)
                VALUES (@project_id, @org_id, @project_slug, @project_name, @embedding_model, @now);
                """;
            insertCmd.Parameters.AddWithValue("org_id", orgId);
            insertCmd.Parameters.AddWithValue("org_slug", Slugify(opts.OrgName));
            insertCmd.Parameters.AddWithValue("org_name", opts.OrgName);
            insertCmd.Parameters.AddWithValue("user_id", userId);
            insertCmd.Parameters.AddWithValue("user_email", opts.UserEmail);
            insertCmd.Parameters.AddWithValue("user_name", opts.UserName);
            insertCmd.Parameters.AddWithValue("project_id", projectId);
            insertCmd.Parameters.AddWithValue("project_slug", Slugify(opts.ProjectName));
            insertCmd.Parameters.AddWithValue("project_name", opts.ProjectName);
            insertCmd.Parameters.AddWithValue("embedding_model", opts.EmbeddingModel);
            insertCmd.Parameters.AddWithValue("now", now);

            await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);

        var tenant = new
        {
            OrganizationId = orgId.ToString("D"),
            UserId = userId.ToString("D"),
            ProjectId = projectId.ToString("D"),
        };
        var tenantJson = JsonSerializer.Serialize(tenant, new JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine($"""
            Bootstrapped tenant scope:
              Organization: {opts.OrgName} ({orgId:D})
              User:         {opts.UserName} <{opts.UserEmail}> ({userId:D})
              Project:      {opts.ProjectName} ({projectId:D})

            Paste this into src/Memory.Mcp.Stdio/appsettings.json under "Tenant":

            {tenantJson}
            """);
        return 0;
    }

    private static InitOptions ParseArgs(string[] args)
    {
        string? connStr = Environment.GetEnvironmentVariable("MEMORY_CONNSTR");
        var orgName = "default-org";
        var userEmail = "user@local";
        var userName = "Local User";
        var projectName = "default";
        var embeddingModel = "text-embedding-3-large";

        for (var i = 0; i < args.Length - 1; i += 2)
        {
            var key = args[i];
            var value = args[i + 1];
            switch (key)
            {
                case "--connection-string": connStr = value; break;
                case "--org": orgName = value; break;
                case "--user-email": userEmail = value; break;
                case "--user-name": userName = value; break;
                case "--project": projectName = value; break;
                case "--embedding-model": embeddingModel = value; break;
                default: throw new ArgumentException($"Unknown flag '{key}'. Run 'memory help' for usage.");
            }
        }

        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new ArgumentException(
                "Connection string required. Pass via --connection-string or set MEMORY_CONNSTR env var.");
        }

        return new InitOptions(connStr, orgName, userEmail, userName, projectName, embeddingModel);
    }

    private static string Slugify(string input)
    {
        var lower = input.ToLowerInvariant();
        var chars = new char[lower.Length];
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            chars[i] = char.IsLetterOrDigit(c) ? c : '-';
        }
        return new string(chars).Trim('-');
    }

    private sealed record InitOptions(
        string ConnectionString,
        string OrgName,
        string UserEmail,
        string UserName,
        string ProjectName,
        string EmbeddingModel);
}
