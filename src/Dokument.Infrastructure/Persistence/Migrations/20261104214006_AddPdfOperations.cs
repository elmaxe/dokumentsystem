using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPdfOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "count",
                table: "renditions",
                type: "integer",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "file_origins",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    source_node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_name = table.Column<string>(type: "text", nullable: false),
                    source_first = table.Column<int>(type: "integer", nullable: false),
                    source_last = table.Column<int>(type: "integer", nullable: false),
                    first_page = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_origins", x => new { x.version_id, x.position });
                    table.ForeignKey(
                        name: "FK_file_origins_file_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "file_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "pdf_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_folder_name = table.Column<string>(type: "text", nullable: true),
                    trash_sources = table.Column<bool>(type: "boolean", nullable: false),
                    sources = table.Column<string>(type: "jsonb", nullable: false),
                    parts = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    pages_done = table.Column<int>(type: "integer", nullable: false),
                    pages_total = table.Column<int>(type: "integer", nullable: false),
                    reserved_bytes = table.Column<long>(type: "bigint", nullable: false),
                    made = table.Column<string>(type: "jsonb", nullable: false),
                    results = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    finished_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pdf_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_pdf_operations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_pdf_operations_user_id_created_at",
                table: "pdf_operations",
                columns: new[] { "user_id", "created_at" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "file_origins");

            migrationBuilder.DropTable(name: "pdf_operations");

            migrationBuilder.DropColumn(name: "count", table: "renditions");
        }
    }
}
