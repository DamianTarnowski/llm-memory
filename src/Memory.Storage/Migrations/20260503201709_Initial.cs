using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "memory");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.ForeignKey(
                        name: "FK_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "memory",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "memory",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => new { x.organization_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "memory",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "memory",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_episodes_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reflections",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generator_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reflections", x => x.id);
                    table.ForeignKey(
                        name: "FK_reflections_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notes",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    context_description = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notes", x => x.id);
                    table.ForeignKey(
                        name: "FK_notes_episodes_source_episode_id",
                        column: x => x.source_episode_id,
                        principalSchema: "memory",
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_notes_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_embeddings",
                schema: "memory",
                columns: table => new
                {
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(3072)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_embeddings", x => x.note_id);
                    table.ForeignKey(
                        name: "FK_note_embeddings_notes_note_id",
                        column: x => x.note_id,
                        principalSchema: "memory",
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_embeddings_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_episodes_ingested_at",
                schema: "memory",
                table: "episodes",
                column: "ingested_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_episodes_project_id",
                schema: "memory",
                table: "episodes",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_memberships_user_id",
                schema: "memory",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_embeddings_project_id",
                schema: "memory",
                table: "note_embeddings",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_notes_project_id",
                schema: "memory",
                table: "notes",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_notes_source_episode_id",
                schema: "memory",
                table: "notes",
                column: "source_episode_id");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_slug",
                schema: "memory",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_organization_id_slug",
                schema: "memory",
                table: "projects",
                columns: new[] { "organization_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reflections_project_id",
                schema: "memory",
                table: "reflections",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                schema: "memory",
                table: "users",
                column: "email",
                unique: true);

            // No vector index in the initial migration: pgvector 0.8.2 caps both HNSW and
            // IVFFlat with vector_cosine_ops at 2,000 dimensions, but our default
            // text-embedding-3-large is 3,072. Sequential scan is fine at < 100k notes.
            // For production scale, follow up with one of:
            //   - HNSW on halfvec(3072) + halfvec_cosine_ops (4,000 dim cap)
            //   - IVFFlat on vector(3072) with vector_l2_ops or vector_ip_ops (16,000 cap)
            //   - text-embedding-3-large with dimensions=1536 (HNSW vector_cosine_ops works)

            migrationBuilder.Sql(
                """
                ALTER TABLE memory.organizations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.organizations FORCE ROW LEVEL SECURITY;
                CREATE POLICY org_tenant_isolation ON memory.organizations
                    USING (id = current_setting('app.organization_id', true)::uuid);

                ALTER TABLE memory.memberships ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.memberships FORCE ROW LEVEL SECURITY;
                CREATE POLICY membership_tenant_isolation ON memory.memberships
                    USING (organization_id = current_setting('app.organization_id', true)::uuid);

                ALTER TABLE memory.projects ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.projects FORCE ROW LEVEL SECURITY;
                CREATE POLICY project_tenant_isolation ON memory.projects
                    USING (organization_id = current_setting('app.organization_id', true)::uuid);

                ALTER TABLE memory.episodes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.episodes FORCE ROW LEVEL SECURITY;
                CREATE POLICY episode_tenant_isolation ON memory.episodes
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.notes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.notes FORCE ROW LEVEL SECURITY;
                CREATE POLICY note_tenant_isolation ON memory.notes
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.note_embeddings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.note_embeddings FORCE ROW LEVEL SECURITY;
                CREATE POLICY note_embedding_tenant_isolation ON memory.note_embeddings
                    USING (project_id = current_setting('app.project_id', true)::uuid);

                ALTER TABLE memory.reflections ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.reflections FORCE ROW LEVEL SECURITY;
                CREATE POLICY reflection_tenant_isolation ON memory.reflections
                    USING (project_id = current_setting('app.project_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memberships",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "note_embeddings",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "reflections",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "users",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "notes",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "episodes",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "memory");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "memory");
        }
    }
}
