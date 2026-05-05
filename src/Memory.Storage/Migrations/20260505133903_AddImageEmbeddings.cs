using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddImageEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "image_embeddings",
                schema: "memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1408)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_embeddings", x => x.id);
                    table.ForeignKey(
                        name: "FK_image_embeddings_notes_note_id",
                        column: x => x.note_id,
                        principalSchema: "memory",
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_image_embeddings_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "memory",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_image_embeddings_note_id",
                schema: "memory",
                table: "image_embeddings",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "IX_image_embeddings_project_id",
                schema: "memory",
                table: "image_embeddings",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "image_embeddings",
                schema: "memory");
        }
    }
}
