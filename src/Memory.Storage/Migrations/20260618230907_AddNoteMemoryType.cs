using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteMemoryType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "memory_type",
                schema: "memory",
                table: "notes",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.Sql("""
                UPDATE memory.notes n
                SET memory_type = CASE
                    WHEN e.source = 'user_preference' THEN 3
                    WHEN e.source IN ('coding_pattern') THEN 2
                    WHEN e.source IN ('doc', 'document', 'markdown', 'blob_document') THEN 4
                    WHEN e.source IN ('ui_test_finding', 'debug_finding', 'memory_hygiene') THEN 1
                    ELSE memory_type
                END
                FROM memory.episodes e
                WHERE n.source_episode_id = e.id
                  AND e.source IN (
                      'user_preference',
                      'coding_pattern',
                      'doc',
                      'document',
                      'markdown',
                      'blob_document',
                      'ui_test_finding',
                      'debug_finding',
                      'memory_hygiene'
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_notes_project_id_memory_type",
                schema: "memory",
                table: "notes",
                columns: new[] { "project_id", "memory_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notes_project_id_memory_type",
                schema: "memory",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "memory_type",
                schema: "memory",
                table: "notes");
        }
    }
}
