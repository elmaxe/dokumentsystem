using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blob_tags",
                columns: table => new
                {
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag_key = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_tags", x => new { x.blob_hash, x.tag_key });
                }
            );

            migrationBuilder.CreateTable(
                name: "node_tag_removals",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_key = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    removed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_tag_removals", x => new { x.node_id, x.tag_key });
                    table.ForeignKey(
                        name: "FK_node_tag_removals_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "node_tags",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_key = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_tags", x => new { x.node_id, x.tag_key });
                    table.ForeignKey(
                        name: "FK_node_tags_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "tag_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    tag_key = table.Column<string>(type: "text", nullable: false),
                    words = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("PK_tag_rules", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_blob_tags_rule_id",
                table: "blob_tags",
                column: "rule_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_blob_tags_tag_key",
                table: "blob_tags",
                column: "tag_key"
            );

            migrationBuilder.CreateIndex(
                name: "IX_node_tags_tag_key",
                table: "node_tags",
                column: "tag_key"
            );

            migrationBuilder.CreateIndex(
                name: "IX_tag_rules_tag_key",
                table: "tag_rules",
                column: "tag_key",
                unique: true
            );

            // What a file shows (docs/design.md §13.4): the tags people added, plus the automatic tags of its current
            // bytes that nobody removed from it and nobody also added by hand.
            migrationBuilder.Sql(
                """
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS effective_node_tags;");

            migrationBuilder.DropTable(name: "blob_tags");

            migrationBuilder.DropTable(name: "node_tag_removals");

            migrationBuilder.DropTable(name: "node_tags");

            migrationBuilder.DropTable(name: "tag_rules");
        }
    }
}
