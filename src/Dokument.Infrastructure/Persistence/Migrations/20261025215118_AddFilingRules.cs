using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "filing_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    tag_key = table.Column<string>(type: "text", nullable: false),
                    folder_template = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    active_since = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_filing_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_filing_rules_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "node_filings",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tag = table.Column<string>(type: "text", nullable: false),
                    from_folder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_folder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    filed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    undone_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_filings", x => x.node_id);
                    table.ForeignKey(
                        name: "FK_node_filings_filing_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "filing_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                    table.ForeignKey(
                        name: "FK_node_filings_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_filing_rules_owner_id_tag_key",
                table: "filing_rules",
                columns: new[] { "owner_id", "tag_key" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_node_filings_filed_at",
                table: "node_filings",
                column: "filed_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_node_filings_rule_id",
                table: "node_filings",
                column: "rule_id"
            );

            // When each file got each tag, for filing rules (docs/design.md §13.5): a tag someone added counts from when
            // they added it, or from when the bytes had it already; an automatic one from when the current bytes got it
            // or were uploaded, whichever is later.
            migrationBuilder.Sql(
                """
                DROP VIEW effective_node_tags;
                CREATE VIEW effective_node_tags AS
                SELECT t.node_id, t.tag, t.tag_key, 'user' AS source, NULL::uuid AS rule_id,
                       LEAST(t.added_at, (
                           SELECT GREATEST(b.created_at, v.uploaded_at)
                           FROM nodes n
                           JOIN file_versions v ON v.id = n.current_version_id
                           JOIN blob_tags b ON b.blob_hash = v.blob_hash AND b.tag_key = t.tag_key
                           WHERE n.id = t.node_id)) AS since
                FROM node_tags t
                UNION ALL
                SELECT n.id, b.tag, b.tag_key, b.source, b.rule_id, GREATEST(b.created_at, v.uploaded_at)
                FROM nodes n
                JOIN file_versions v ON v.id = n.current_version_id
                JOIN blob_tags b ON b.blob_hash = v.blob_hash
                WHERE NOT EXISTS (SELECT 1 FROM node_tags t WHERE t.node_id = n.id AND t.tag_key = b.tag_key)
                  AND NOT EXISTS (SELECT 1 FROM node_tag_removals r WHERE r.node_id = n.id AND r.tag_key = b.tag_key);
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP VIEW effective_node_tags;
                CREATE VIEW effective_node_tags AS
                SELECT t.node_id, t.tag, t.tag_key, 'user' AS source, NULL::uuid AS rule_id
                FROM node_tags t
                UNION ALL
                SELECT n.id, b.tag, b.tag_key, b.source, b.rule_id
                FROM nodes n
                JOIN file_versions v ON v.id = n.current_version_id
                JOIN blob_tags b ON b.blob_hash = v.blob_hash
                WHERE NOT EXISTS (SELECT 1 FROM node_tags t WHERE t.node_id = n.id AND t.tag_key = b.tag_key)
                  AND NOT EXISTS (SELECT 1 FROM node_tag_removals r WHERE r.node_id = n.id AND r.tag_key = b.tag_key);
                """
            );

            migrationBuilder.DropTable(name: "node_filings");

            migrationBuilder.DropTable(name: "filing_rules");
        }
    }
}
