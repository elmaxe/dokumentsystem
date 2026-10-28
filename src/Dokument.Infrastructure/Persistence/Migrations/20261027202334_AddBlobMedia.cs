using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlobMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blob_media",
                columns: table => new
                {
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    container = table.Column<string>(type: "text", nullable: false),
                    duration_seconds = table.Column<double>(
                        type: "double precision",
                        nullable: true
                    ),
                    has_video = table.Column<bool>(type: "boolean", nullable: false),
                    has_audio = table.Column<bool>(type: "boolean", nullable: false),
                    video_codec = table.Column<string>(type: "text", nullable: true),
                    audio_codec = table.Column<string>(type: "text", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    plan = table.Column<string>(type: "text", nullable: false),
                    copy_status = table.Column<string>(type: "text", nullable: false),
                    copy_progress = table.Column<int>(type: "integer", nullable: true),
                    copy_error = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_media", x => x.blob_hash);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "blob_media");
        }
    }
}
