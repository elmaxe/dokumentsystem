using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dokument.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BlobsPerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_blob_previews_blobs_blob_hash",
                table: "blob_previews"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_file_versions_blobs_blob_hash",
                table: "file_versions"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_renditions_blobs_blob_hash",
                table: "renditions"
            );

            migrationBuilder.DropPrimaryKey(name: "PK_blobs", table: "blobs");

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "blobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000")
            );

            // Nothing is in use yet, so existing blobs aren't moved: they keep their old storage key and go to the owner
            // of a file that uses them. Blobs nothing uses are dropped (their files are left for the integrity job).
            migrationBuilder.Sql(
                """
                UPDATE blobs b SET owner_id = n.owner_id
                FROM (
                    SELECT DISTINCT ON (v.blob_hash) v.blob_hash, n.owner_id
                    FROM file_versions v JOIN nodes n ON n.id = v.node_id
                    ORDER BY v.blob_hash, v.uploaded_at
                ) n
                WHERE n.blob_hash = b.hash;
                DELETE FROM blobs WHERE owner_id = '00000000-0000-0000-0000-000000000000';
                """
            );

            migrationBuilder.AddPrimaryKey(
                name: "PK_blobs",
                table: "blobs",
                columns: new[] { "owner_id", "hash" }
            );

            migrationBuilder.CreateIndex(name: "IX_blobs_hash", table: "blobs", column: "hash");

            migrationBuilder.AddForeignKey(
                name: "FK_blobs_users_owner_id",
                table: "blobs",
                column: "owner_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_blobs_users_owner_id", table: "blobs");

            migrationBuilder.DropPrimaryKey(name: "PK_blobs", table: "blobs");

            migrationBuilder.DropIndex(name: "IX_blobs_hash", table: "blobs");

            migrationBuilder.DropColumn(name: "owner_id", table: "blobs");

            migrationBuilder.AddPrimaryKey(name: "PK_blobs", table: "blobs", column: "hash");

            migrationBuilder.AddForeignKey(
                name: "FK_blob_previews_blobs_blob_hash",
                table: "blob_previews",
                column: "blob_hash",
                principalTable: "blobs",
                principalColumn: "hash",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_file_versions_blobs_blob_hash",
                table: "file_versions",
                column: "blob_hash",
                principalTable: "blobs",
                principalColumn: "hash",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_renditions_blobs_blob_hash",
                table: "renditions",
                column: "blob_hash",
                principalTable: "blobs",
                principalColumn: "hash",
                onDelete: ReferentialAction.Cascade
            );
        }
    }
}
