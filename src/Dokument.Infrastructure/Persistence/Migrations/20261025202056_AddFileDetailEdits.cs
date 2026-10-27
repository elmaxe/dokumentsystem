using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileDetailEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_content_pages_blob_hash_page_no",
                table: "content_pages"
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "edited_at",
                table: "content_pages",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "edited_by",
                table: "content_pages",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "version_id",
                table: "content_pages",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                table: "blob_metadata",
                type: "double precision",
                nullable: true
            );

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                table: "blob_metadata",
                type: "double precision",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "taken_offset_minutes",
                table: "blob_metadata",
                type: "integer",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "version_details",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    taken_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    taken_precision = table.Column<string>(type: "text", nullable: true),
                    edited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_version_details", x => x.version_id);
                    table.ForeignKey(
                        name: "FK_version_details_file_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "file_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_content_pages_blob_hash_page_no",
                table: "content_pages",
                columns: new[] { "blob_hash", "page_no" },
                unique: true,
                filter: "version_id IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "IX_content_pages_version_id_page_no",
                table: "content_pages",
                columns: new[] { "version_id", "page_no" },
                unique: true,
                filter: "version_id IS NOT NULL"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_content_pages_file_versions_version_id",
                table: "content_pages",
                column: "version_id",
                principalTable: "file_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_content_pages_file_versions_version_id",
                table: "content_pages"
            );

            migrationBuilder.DropTable(name: "version_details");

            migrationBuilder.DropIndex(
                name: "IX_content_pages_blob_hash_page_no",
                table: "content_pages"
            );

            migrationBuilder.DropIndex(
                name: "IX_content_pages_version_id_page_no",
                table: "content_pages"
            );

            migrationBuilder.DropColumn(name: "edited_at", table: "content_pages");

            migrationBuilder.DropColumn(name: "edited_by", table: "content_pages");

            migrationBuilder.DropColumn(name: "version_id", table: "content_pages");

            migrationBuilder.DropColumn(name: "latitude", table: "blob_metadata");

            migrationBuilder.DropColumn(name: "longitude", table: "blob_metadata");

            migrationBuilder.DropColumn(name: "taken_offset_minutes", table: "blob_metadata");

            migrationBuilder.CreateIndex(
                name: "IX_content_pages_blob_hash_page_no",
                table: "content_pages",
                columns: new[] { "blob_hash", "page_no" },
                unique: true
            );
        }
    }
}
