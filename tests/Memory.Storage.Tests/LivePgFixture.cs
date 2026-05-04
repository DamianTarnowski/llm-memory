using Memory.Domain;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Memory.Storage.Tests;

/// <summary>
/// Provisions a clean Postgres database (drop + create + migrate + AGE setup + tenant seed)
/// per test session. Shared across all tests in the LivePg collection.
/// </summary>
public sealed class LivePgFixture : IAsyncLifetime
{
    private const string AdminConnString = "Host=localhost;Port=5435;Database=postgres;Username=postgres;Password=REDACTED_PG_PASSWORD";
    public const string TestDbName = "llm_memory_test";
    public string ConnectionString => $"Host=localhost;Port=5435;Database={TestDbName};Username=postgres;Password=REDACTED_PG_PASSWORD";

    public OrganizationId Org { get; } = new(Guid.Parse("a1111111-1111-1111-1111-111111111111"));
    public UserId User { get; } = new(Guid.Parse("a2222222-2222-2222-2222-222222222222"));
    public ProjectId Project { get; } = new(Guid.Parse("a3333333-3333-3333-3333-333333333333"));

    public ServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await ResetDatabaseAsync();
        await BootstrapAgeAndExtensionsAsync();
        BuildServiceProvider();
        await ApplyMigrationsAsync();
        await SeedTenantAsync();
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
        {
            await Services.DisposeAsync();
        }
    }

    private static async Task ResetDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await Exec(conn, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{TestDbName}' AND pid <> pg_backend_pid();");
        await Exec(conn, $"DROP DATABASE IF EXISTS {TestDbName};");
        await Exec(conn, $"CREATE DATABASE {TestDbName};");
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task BootstrapAgeAndExtensionsAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE EXTENSION IF NOT EXISTS age;
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $$ BEGIN
              PERFORM ag_catalog.create_graph('memory_graph');
            EXCEPTION WHEN duplicate_schema THEN NULL; WHEN duplicate_object THEN NULL;
            END $$;
            CREATE SCHEMA IF NOT EXISTS memory;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private void BuildServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ConnectionString"] = ConnectionString,
                ["Storage:GraphName"] = "memory_graph",
                ["Storage:Schema"] = "memory",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMemoryTenancy();
        services.AddMemoryStorage(configuration);

        Services = services.BuildServiceProvider();
    }

    private async Task ApplyMigrationsAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.Database.MigrateAsync();
    }

    private async Task SeedTenantAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SET app.organization_id = '{Org.Value:D}';
            SET app.project_id = '{Project.Value:D}';
            INSERT INTO memory.organizations (id, slug, name, created_at)
                VALUES ('{Org.Value:D}', 'test-org', 'Test Org', now());
            INSERT INTO memory.users (id, email, display_name, created_at)
                VALUES ('{User.Value:D}', 'test@example.com', 'Test User', now());
            INSERT INTO memory.memberships (organization_id, user_id, role, granted_at)
                VALUES ('{Org.Value:D}', '{User.Value:D}', 0, now());
            INSERT INTO memory.projects (id, organization_id, slug, name, embedding_model, created_at)
                VALUES ('{Project.Value:D}', '{Org.Value:D}', 'test-project', 'Test Project', 'text-embedding-3-large', now());
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public IDisposable BeginTestScope()
    {
        var tenant = Services.GetRequiredService<ITenantContext>();
        return tenant.BeginScope(new TenantScope(Org, User, Project));
    }
}

[CollectionDefinition(nameof(LivePgCollection))]
public sealed class LivePgCollection : ICollectionFixture<LivePgFixture> { }
