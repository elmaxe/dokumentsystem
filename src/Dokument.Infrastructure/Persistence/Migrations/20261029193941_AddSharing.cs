using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "trashed_by",
                table: "nodes",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "skipped",
                table: "node_filings",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateTable(
                name: "shares",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    seen_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shares", x => x.id);
                    table.ForeignKey(
                        name: "FK_shares_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_shares_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_shares_users_grantee_user_id",
                        column: x => x.grantee_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_shares_created_by",
                table: "shares",
                column: "created_by"
            );

            migrationBuilder.CreateIndex(
                name: "IX_shares_grantee_user_id",
                table: "shares",
                column: "grantee_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_shares_node_id_grantee_user_id",
                table: "shares",
                columns: new[] { "node_id", "grantee_user_id" },
                unique: true
            );

            // NodeAccess.SharedRole (docs/design.md §9): the highest role the shares on a node or the folders above it
            // give a person, 0 for none. A plain SQL function, so Postgres can inline it into the queries that use it.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION shared_role(p_user uuid, p_path ltree) RETURNS integer
                LANGUAGE sql STABLE PARALLEL SAFE AS $$
                    SELECT COALESCE(max(CASE s.role WHEN 'editor' THEN 2 ELSE 1 END), 0)
                    FROM shares s JOIN nodes sn ON sn.id = s.node_id
                    WHERE s.grantee_user_id = p_user AND sn.path @> p_path
                $$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION shared_role(uuid, ltree);");

            migrationBuilder.DropTable(name: "shares");

            migrationBuilder.DropColumn(name: "trashed_by", table: "nodes");

            migrationBuilder.DropColumn(name: "skipped", table: "node_filings");
        }
    }
}
