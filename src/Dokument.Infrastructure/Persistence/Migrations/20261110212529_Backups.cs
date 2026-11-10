using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Backups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    scheduled_for = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    status = table.Column<string>(type: "text", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    started_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    finished_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    locked_until = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    name = table.Column<string>(type: "text", nullable: true),
                    database_bytes = table.Column<long>(type: "bigint", nullable: true),
                    manifest_bytes = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    error_detail = table.Column<string>(type: "text", nullable: true),
                    dismissed = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backups", x => x.id);
                    table.ForeignKey(
                        name: "FK_backups_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_backups_queued_at",
                table: "backups",
                column: "queued_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_backups_requested_by",
                table: "backups",
                column: "requested_by"
            );

            migrationBuilder.CreateIndex(
                name: "IX_backups_scheduled_for",
                table: "backups",
                column: "scheduled_for",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "backups");
        }
    }
}
