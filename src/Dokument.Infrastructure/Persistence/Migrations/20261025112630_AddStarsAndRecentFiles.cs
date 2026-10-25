using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStarsAndRecentFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recent_files",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    action = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recent_files", x => new { x.user_id, x.node_id });
                    table.ForeignKey(
                        name: "FK_recent_files_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_recent_files_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "stars",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starred_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stars", x => new { x.user_id, x.node_id });
                    table.ForeignKey(
                        name: "FK_stars_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_stars_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_recent_files_node_id",
                table: "recent_files",
                column: "node_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_recent_files_user_id_at",
                table: "recent_files",
                columns: new[] { "user_id", "at" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "IX_stars_node_id",
                table: "stars",
                column: "node_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "recent_files");

            migrationBuilder.DropTable(name: "stars");
        }
    }
}
