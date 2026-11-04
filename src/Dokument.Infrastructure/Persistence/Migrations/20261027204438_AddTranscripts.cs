using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "end_ms",
                table: "content_pages",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "start_ms",
                table: "content_pages",
                type: "integer",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "blob_transcripts",
                columns: table => new
                {
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    engine = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    done_ms = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_transcripts", x => x.blob_hash);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "blob_transcripts");

            migrationBuilder.DropColumn(name: "end_ms", table: "content_pages");

            migrationBuilder.DropColumn(name: "start_ms", table: "content_pages");
        }
    }
}
