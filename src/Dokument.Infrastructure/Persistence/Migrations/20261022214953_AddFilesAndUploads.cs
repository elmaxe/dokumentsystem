using System;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilesAndUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase().Annotation("Npgsql:PostgresExtension:ltree", ",,");

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<JsonObject>(type: "jsonb", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "blobs",
                columns: table => new
                {
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    ref_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blobs", x => x.hash);
                }
            );

            migrationBuilder.CreateTable(
                name: "uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    upload_offset = table.Column<long>(type: "bigint", nullable: false),
                    claimed_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    mime_type = table.Column<string>(type: "text", nullable: true),
                    client_modified_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    on_conflict = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    expires_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    result_node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uploads", x => x.id);
                    table.ForeignKey(
                        name: "FK_uploads_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "file_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    blob_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    original_name = table.Column<string>(type: "text", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    client_modified_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    restored_from = table.Column<int>(type: "integer", nullable: true),
                    keep_forever = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_file_versions_blobs_blob_hash",
                        column: x => x.blob_hash,
                        principalTable: "blobs",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_file_versions_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_key = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "ltree", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    modified_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    trashed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    trashed_root_id = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_nodes_file_versions_current_version_id",
                        column: x => x.current_version_id,
                        principalTable: "file_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_nodes_nodes_parent_id",
                        column: x => x.parent_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_nodes_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_at",
                table: "audit_events",
                column: "at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_target_node_id_at",
                table: "audit_events",
                columns: new[] { "target_node_id", "at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_blob_hash",
                table: "file_versions",
                column: "blob_hash"
            );

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_node_id_version_no",
                table: "file_versions",
                columns: new[] { "node_id", "version_no" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_uploaded_by",
                table: "file_versions",
                column: "uploaded_by"
            );

            migrationBuilder.CreateIndex(
                name: "IX_nodes_current_version_id",
                table: "nodes",
                column: "current_version_id",
                unique: true
            );

            migrationBuilder
                .CreateIndex(
                    name: "IX_nodes_owner_id_parent_id_name_key",
                    table: "nodes",
                    columns: new[] { "owner_id", "parent_id", "name_key" },
                    unique: true,
                    filter: "trashed_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_nodes_parent_id",
                table: "nodes",
                column: "parent_id"
            );

            migrationBuilder
                .CreateIndex(name: "IX_nodes_path", table: "nodes", column: "path")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_uploads_expires_at",
                table: "uploads",
                column: "expires_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_uploads_user_id",
                table: "uploads",
                column: "user_id"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_file_versions_nodes_node_id",
                table: "file_versions",
                column: "node_id",
                principalTable: "nodes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_file_versions_blobs_blob_hash",
                table: "file_versions"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_file_versions_nodes_node_id",
                table: "file_versions"
            );

            migrationBuilder.DropTable(name: "audit_events");

            migrationBuilder.DropTable(name: "uploads");

            migrationBuilder.DropTable(name: "blobs");

            migrationBuilder.DropTable(name: "nodes");

            migrationBuilder.DropTable(name: "file_versions");

            migrationBuilder.AlterDatabase().OldAnnotation("Npgsql:PostgresExtension:ltree", ",,");
        }
    }
}
