using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "harvested_sessions",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    transcript_gzip = table.Column<byte[]>(type: "bytea", nullable: true),
                    transcript_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    stats = table.Column<string>(type: "jsonb", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_harvested_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_harvested_sessions_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    when_to_use = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    frontmatter_extra = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    helpful_count = table.Column<int>(type: "integer", nullable: false),
                    harmful_count = table.Column<int>(type: "integer", nullable: false),
                    usage_count = table.Column<int>(type: "integer", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deprecated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    generator_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    untrusted_input = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.id);
                    table.ForeignKey(
                        name: "FK_skills_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skill_embeddings",
                schema: "memory",
                columns: table => new
                {
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(3072)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_embeddings", x => x.skill_id);
                    table.ForeignKey(
                        name: "FK_skill_embeddings_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_skill_embeddings_skills_skill_id",
                        column: x => x.skill_id,
                        principalSchema: "memory",
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skill_provenance",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_provenance", x => x.id);
                    table.ForeignKey(
                        name: "FK_skill_provenance_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_skill_provenance_skills_skill_id",
                        column: x => x.skill_id,
                        principalSchema: "memory",
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skill_usage_events",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    session_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_usage_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_skill_usage_events_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_skill_usage_events_skills_skill_id",
                        column: x => x.skill_id,
                        principalSchema: "memory",
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skill_versions",
                schema: "memory",
                columns: table => new
                {
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    when_to_use = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    frontmatter_extra = table.Column<string>(type: "jsonb", nullable: true),
                    change_summary = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_versions", x => new { x.skill_id, x.version });
                    table.ForeignKey(
                        name: "FK_skill_versions_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_skill_versions_skills_skill_id",
                        column: x => x.skill_id,
                        principalSchema: "memory",
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_harvested_sessions_project_id_content_hash",
                schema: "memory",
                table: "harvested_sessions",
                columns: new[] { "project_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_harvested_sessions_project_id_status",
                schema: "memory",
                table: "harvested_sessions",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_skill_embeddings_project_id",
                schema: "memory",
                table: "skill_embeddings",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_provenance_project_id",
                schema: "memory",
                table: "skill_provenance",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_provenance_skill_id",
                schema: "memory",
                table: "skill_provenance",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_usage_events_project_id",
                schema: "memory",
                table: "skill_usage_events",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_usage_events_skill_id",
                schema: "memory",
                table: "skill_usage_events",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_project_id",
                schema: "memory",
                table: "skill_versions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_skills_project_id_name",
                schema: "memory",
                table: "skills",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_project_id_status",
                schema: "memory",
                table: "skills",
                columns: new[] { "project_id", "status" });

            // Tenant isolation: every skills table carries a denormalized project_id and
            // gets the standard project-GUC RLS policy (same shape as notes/note_relations).
            migrationBuilder.Sql(
                """
                ALTER TABLE memory.skills ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.skills FORCE ROW LEVEL SECURITY;
                CREATE POLICY skill_tenant_isolation ON memory.skills
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.skill_versions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.skill_versions FORCE ROW LEVEL SECURITY;
                CREATE POLICY skill_version_tenant_isolation ON memory.skill_versions
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.skill_provenance ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.skill_provenance FORCE ROW LEVEL SECURITY;
                CREATE POLICY skill_provenance_tenant_isolation ON memory.skill_provenance
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.skill_embeddings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.skill_embeddings FORCE ROW LEVEL SECURITY;
                CREATE POLICY skill_embedding_tenant_isolation ON memory.skill_embeddings
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.skill_usage_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.skill_usage_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY skill_usage_event_tenant_isolation ON memory.skill_usage_events
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.harvested_sessions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.harvested_sessions FORCE ROW LEVEL SECURITY;
                CREATE POLICY harvested_session_tenant_isolation ON memory.harvested_sessions
                    USING (project_id = current_setting('app.project_id', true)::uuid);
                """);

            // Defensive grants: ALTER DEFAULT PRIVILEGES from AddMemoryAppRole normally
            // covers new tables, but default privileges bind to the role that ran that
            // migration — an environment migrated by a different owner role would leave
            // these tables inaccessible to the runtime. Idempotent, harmless when redundant.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'memory_app') THEN
                        GRANT SELECT, INSERT, UPDATE, DELETE ON
                            memory.skills,
                            memory.skill_versions,
                            memory.skill_provenance,
                            memory.skill_embeddings,
                            memory.skill_usage_events,
                            memory.harvested_sessions
                        TO memory_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "harvested_sessions",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "skill_embeddings",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "skill_provenance",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "skill_usage_events",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "skill_versions",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "skills",
                schema: "memory");
        }
    }
}
