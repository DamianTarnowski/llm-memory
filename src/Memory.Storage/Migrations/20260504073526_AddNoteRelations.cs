using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_relations",
                schema: "memory",
                columns: table => new
                {
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    similarity = table.Column<double>(type: "double precision", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_relations", x => new { x.note_id, x.related_note_id });
                    table.ForeignKey(
                        name: "FK_note_relations_notes_note_id",
                        column: x => x.note_id,
                        principalSchema: "memory",
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_relations_notes_related_note_id",
                        column: x => x.related_note_id,
                        principalSchema: "memory",
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_relations_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_note_relations_project_id",
                schema: "memory",
                table: "note_relations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_relations_related_note_id",
                schema: "memory",
                table: "note_relations",
                column: "related_note_id");

            migrationBuilder.Sql(
                """
                ALTER TABLE memory.note_relations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.note_relations FORCE ROW LEVEL SECURITY;
                CREATE POLICY note_relation_tenant_isolation ON memory.note_relations
                    USING (project_id = current_setting('app.project_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_relations",
                schema: "memory");
        }
    }
}
