using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Memory.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteFullTextIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Generated stored tsvector over content + context + keywords + tags.
            // 'simple' config keeps it language-agnostic (no stemming) — handles Polish/English mix.
            // PG treats to_tsvector as STABLE (regconfig depends on session), so generated columns
            // reject it. Use a regular column populated by a BEFORE trigger and backfill existing rows.
            migrationBuilder.Sql(
                """
                ALTER TABLE memory.notes ADD COLUMN content_tsv tsvector;

                CREATE OR REPLACE FUNCTION memory.notes_set_tsv() RETURNS trigger AS $$
                BEGIN
                  NEW.content_tsv :=
                    setweight(to_tsvector('simple', coalesce(NEW.content, '')), 'A') ||
                    setweight(to_tsvector('simple', coalesce(NEW.context_description, '')), 'B') ||
                    setweight(to_tsvector('simple', array_to_string(coalesce(NEW.keywords, ARRAY[]::text[]), ' ')), 'C') ||
                    setweight(to_tsvector('simple', array_to_string(coalesce(NEW.tags, ARRAY[]::text[]), ' ')), 'D');
                  RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER notes_set_tsv_trigger
                  BEFORE INSERT OR UPDATE OF content, context_description, keywords, tags
                  ON memory.notes
                  FOR EACH ROW EXECUTE FUNCTION memory.notes_set_tsv();

                -- Backfill existing rows
                UPDATE memory.notes SET content = content;

                CREATE INDEX ix_notes_content_tsv ON memory.notes USING gin (content_tsv);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS memory.ix_notes_content_tsv;
                ALTER TABLE memory.notes DROP COLUMN IF EXISTS content_tsv;
                """);
        }
    }
}
