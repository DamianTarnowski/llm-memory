using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteEntityMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_entity_mentions",
                schema: "memory",
                columns: table => new
                {
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_entity_mentions", x => new { x.note_id, x.entity_id });
                    table.ForeignKey(
                        name: "FK_note_entity_mentions_notes_note_id",
                        column: x => x.note_id,
                        principalSchema: "memory",
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_entity_mentions_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_note_entity_mentions_entity_id",
                schema: "memory",
                table: "note_entity_mentions",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_entity_mentions_project_id",
                schema: "memory",
                table: "note_entity_mentions",
                column: "project_id");

            migrationBuilder.Sql(
                """
                ALTER TABLE memory.note_entity_mentions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE memory.note_entity_mentions FORCE ROW LEVEL SECURITY;
                CREATE POLICY note_entity_mention_tenant_isolation ON memory.note_entity_mentions
                    USING (project_id = current_setting('app.project_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_entity_mentions",
                schema: "memory");
        }
    }
}
