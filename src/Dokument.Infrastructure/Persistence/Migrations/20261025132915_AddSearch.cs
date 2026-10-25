using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blob_contents",
                columns: table => new
                {
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    pipeline_version = table.Column<int>(type: "integer", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    ocr_skipped = table.Column<bool>(type: "boolean", nullable: false),
                    ocr_engine = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_contents", x => x.blob_hash);
                }
            );

            migrationBuilder.CreateTable(
                name: "blob_metadata",
                columns: table => new
                {
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    pipeline_version = table.Column<int>(type: "integer", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    camera = table.Column<string>(type: "text", nullable: true),
                    duration_seconds = table.Column<double>(
                        type: "double precision",
                        nullable: true
                    ),
                    tags = table.Column<string>(type: "jsonb", nullable: false),
                    readable = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_metadata", x => x.blob_hash);
                }
            );

            migrationBuilder.CreateTable(
                name: "content_pages",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
                        ),
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    page_no = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    words = table.Column<string>(type: "jsonb", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_pages", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_content_pages_blob_hash_page_no",
                table: "content_pages",
                columns: new[] { "blob_hash", "page_no" },
                unique: true
            );

            // BM25 indexes (docs/design.md §13.1). Page text is indexed three ways, so a word matches as itself, as a
            // Swedish stem and as an English stem, without guessing each page's language. Names are split on anything
            // that isn't a letter or digit ("IKEA-kvitto.pdf" is "ikea", "kvitto", "pdf"), and as trigrams so part of a
            // word ("4471" in "IMG_4471") matches too.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_content_pages_bm25 ON content_pages USING bm25 (
                    id,
                    (text::pdb.simple('alias=text_raw')),
                    (text::pdb.simple('stemmer=swedish', 'alias=text_sv')),
                    (text::pdb.simple('stemmer=english', 'alias=text_en'))
                ) WITH (key_field = 'id');
                """
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_nodes_bm25 ON nodes USING bm25 (
                    id,
                    (name::pdb.simple('alias=name_words')),
                    (name::pdb.ngram(3, 3, 'alias=name_ngram'))
                ) WITH (key_field = 'id');
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_nodes_bm25;");

            migrationBuilder.DropTable(name: "blob_contents");

            migrationBuilder.DropTable(name: "blob_metadata");

            migrationBuilder.DropTable(name: "content_pages");
        }
    }
}
