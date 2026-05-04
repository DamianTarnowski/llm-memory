using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Creates a non-superuser <c>memory_app</c> postgres role for runtime API/MCP queries.
    ///
    /// Why: postgres superuser bypasses row-level security regardless of FORCE ROW LEVEL
    /// SECURITY on tables. Connecting the API as superuser silently disables tenancy
    /// isolation — RLS policies are present but skipped, so any (or no) tenant header
    /// returns every project's rows.
    ///
    /// Migrations themselves still run as the superuser whose connection string is in
    /// appsettings (needed for CREATE ROLE, EXTENSION, schema DDL). The runtime app
    /// connection string should be flipped to memory_app so RLS is enforced. Default
    /// password matches the dev credential pattern; production deployments should
    /// rotate via ALTER ROLE memory_app PASSWORD '...'.
    /// </summary>
    public partial class AddMemoryAppRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'memory_app') THEN
                        CREATE ROLE memory_app LOGIN PASSWORD 'memory_app' NOBYPASSRLS;
                    END IF;
                END $$;

                GRANT USAGE ON SCHEMA memory TO memory_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA memory TO memory_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA memory TO memory_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA memory
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO memory_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA memory
                    GRANT USAGE, SELECT ON SEQUENCES TO memory_app;

                -- Apache AGE: cypher() lives in ag_catalog and the per-graph schema holds
                -- the _ag_label_* storage tables that the AgeGraphContext mutates.
                GRANT USAGE ON SCHEMA ag_catalog TO memory_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ag_catalog TO memory_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ag_catalog TO memory_app;

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'memory_graph') THEN
                        EXECUTE 'GRANT USAGE ON SCHEMA memory_graph TO memory_app';
                        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA memory_graph TO memory_app';
                        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA memory_graph TO memory_app';
                        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA memory_graph GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO memory_app';
                        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA memory_graph GRANT USAGE, SELECT ON SEQUENCES TO memory_app';
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'memory_app') THEN
                        EXECUTE 'REASSIGN OWNED BY memory_app TO postgres';
                        EXECUTE 'DROP OWNED BY memory_app';
                        EXECUTE 'DROP ROLE memory_app';
                    END IF;
                END $$;
                """);
        }
    }
}
