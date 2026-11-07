using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilingRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                table: "node_filings",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "filing_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    node_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    include_placed = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    done = table.Column<int>(type: "integer", nullable: false),
                    moved = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    finished_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    undone_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    stop_requested = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_filing_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_filing_runs_filing_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "filing_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                    table.ForeignKey(
                        name: "FK_filing_runs_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_node_filings_run_id",
                table: "node_filings",
                column: "run_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_filing_runs_owner_id_created_at",
                table: "filing_runs",
                columns: new[] { "owner_id", "created_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_filing_runs_rule_id",
                table: "filing_runs",
                column: "rule_id"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_node_filings_filing_runs_run_id",
                table: "node_filings",
                column: "run_id",
                principalTable: "filing_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_node_filings_filing_runs_run_id",
                table: "node_filings"
            );

            migrationBuilder.DropTable(name: "filing_runs");

            migrationBuilder.DropIndex(name: "IX_node_filings_run_id", table: "node_filings");

            migrationBuilder.DropColumn(name: "run_id", table: "node_filings");
        }
    }
}
