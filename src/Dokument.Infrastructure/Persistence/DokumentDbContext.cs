using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dokument.Infrastructure.Persistence;

public sealed class DokumentDbContext(DbContextOptions<DokumentDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("ltree");

        modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasColumnName("key");
            e.Property(s => s.Value).HasColumnName("value").HasColumnType("jsonb");
            e.Property(s => s.UpdatedBy).HasColumnName("updated_by");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id");
            e.Property(u => u.OidcIssuer).HasColumnName("oidc_issuer");
            e.Property(u => u.OidcSubject).HasColumnName("oidc_subject");
            e.Property(u => u.Email).HasColumnName("email");
            e.Property(u => u.DisplayName).HasColumnName("display_name");
            e.Property(u => u.Role).HasColumnName("role").HasConversion(LowercaseEnum<UserRole>());
            e.Property(u => u.QuotaBytes).HasColumnName("quota_bytes");
            e.Property(u => u.UsedBytes).HasColumnName("used_bytes");
            e.Property(u => u.ReservedBytes).HasColumnName("reserved_bytes");
            e.Property(u => u.Locale).HasColumnName("locale");
            e.Property(u => u.CreatedAt).HasColumnName("created_at");
            e.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
            e.Property(u => u.DisabledAt).HasColumnName("disabled_at");
            e.HasIndex(u => new { u.OidcIssuer, u.OidcSubject }).IsUnique();
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.Kind).HasColumnName("kind").HasConversion(LowercaseEnum<SessionKind>());
            e.Property(s => s.TokenHash).HasColumnName("token_hash");
            e.Property(s => s.DeviceName).HasColumnName("device_name").HasConversion(d => d!.Value, v => DeviceName.From(v));
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            e.Property(s => s.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.UserId);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureFiles(modelBuilder);
    }

    private static void ConfigureFiles(ModelBuilder modelBuilder)
    {
        var nodeName = new ValueConverter<NodeName, string>(n => n.Value, v => NodeName.From(v));
        var contentHash = new ValueConverter<ContentHash, byte[]>(h => h.ToArray(), v => ContentHash.FromBytes(v));

        modelBuilder.Entity<Node>(e =>
        {
            e.ToTable("nodes");
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasColumnName("id");
            e.Property(n => n.OwnerId).HasColumnName("owner_id");
            e.Property(n => n.ParentId).HasColumnName("parent_id");
            e.Property(n => n.Kind).HasColumnName("kind").HasConversion(LowercaseEnum<NodeKind>());
            e.Property(n => n.Name).HasColumnName("name").HasConversion(nodeName);
            e.Property(n => n.NameKey).HasColumnName("name_key");
            e.Property(n => n.Path).HasColumnName("path").HasColumnType("ltree");
            e.Property(n => n.CurrentVersionId).HasColumnName("current_version_id");
            e.Property(n => n.CreatedAt).HasColumnName("created_at");
            e.Property(n => n.ModifiedAt).HasColumnName("modified_at");
            e.Property(n => n.TrashedAt).HasColumnName("trashed_at");
            e.Property(n => n.TrashedRootId).HasColumnName("trashed_root_id");
            e.HasOne<User>().WithMany().HasForeignKey(n => n.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Node>().WithMany().HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.CurrentVersion).WithOne().HasForeignKey<Node>(n => n.CurrentVersionId).OnDelete(DeleteBehavior.Restrict);

            // Names are unique among the live children of a folder; the root (parent null) counts as one folder per owner.
            e.HasIndex(n => new { n.OwnerId, n.ParentId, n.NameKey }).IsUnique().AreNullsDistinct(false).HasFilter("trashed_at IS NULL");
            e.HasIndex(n => n.Path).HasMethod("gist");
        });

        modelBuilder.Entity<FileVersion>(e =>
        {
            e.ToTable("file_versions");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).HasColumnName("id");
            e.Property(v => v.NodeId).HasColumnName("node_id");
            e.Property(v => v.Number).HasColumnName("version_no");
            e.Property(v => v.BlobHash).HasColumnName("blob_hash").HasConversion(contentHash);
            e.Property(v => v.Size).HasColumnName("size");
            e.Property(v => v.MimeType).HasColumnName("mime_type");
            e.Property(v => v.OriginalName).HasColumnName("original_name").HasConversion(nodeName);
            e.Property(v => v.UploadedBy).HasColumnName("uploaded_by");
            e.Property(v => v.UploadedAt).HasColumnName("uploaded_at");
            e.Property(v => v.ClientModifiedAt).HasColumnName("client_modified_at");
            e.Property(v => v.RestoredFrom).HasColumnName("restored_from");
            e.Property(v => v.KeepForever).HasColumnName("keep_forever");
            e.HasOne<Node>().WithMany().HasForeignKey(v => v.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Blob>().WithMany().HasForeignKey(v => v.BlobHash).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(v => v.UploadedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(v => new { v.NodeId, v.Number }).IsUnique();
            e.HasIndex(v => v.BlobHash);
        });

        modelBuilder.Entity<Blob>(e =>
        {
            e.ToTable("blobs");
            e.HasKey(b => b.Hash);
            e.Property(b => b.Hash).HasColumnName("hash").HasConversion(contentHash);
            e.Property(b => b.Size).HasColumnName("size");
            e.Property(b => b.StorageKey).HasColumnName("storage_key").HasConversion(k => k.Value, v => StorageKey.From(v));
            e.Property(b => b.RefCount).HasColumnName("ref_count");
            e.Property(b => b.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<Upload>(e =>
        {
            e.ToTable("uploads");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id");
            e.Property(u => u.UserId).HasColumnName("user_id");
            e.Property(u => u.OwnerId).HasColumnName("owner_id");
            e.Property(u => u.ParentId).HasColumnName("parent_id");
            e.Property(u => u.TargetNodeId).HasColumnName("target_node_id");
            e.Property(u => u.Name).HasColumnName("name").HasConversion(nodeName);
            e.Property(u => u.Size).HasColumnName("size");
            e.Property(u => u.Offset).HasColumnName("upload_offset");
            e.Property(u => u.ClaimedHash).HasColumnName("claimed_hash").HasConversion(contentHash!);
            e.Property(u => u.MimeType).HasColumnName("mime_type");
            e.Property(u => u.ClientModifiedAt).HasColumnName("client_modified_at");
            e.Property(u => u.OnConflict).HasColumnName("on_conflict").HasConversion(LowercaseEnum<NameConflictPolicy>());
            e.Property(u => u.CreatedAt).HasColumnName("created_at");
            e.Property(u => u.ExpiresAt).HasColumnName("expires_at");
            e.Property(u => u.ResultNodeId).HasColumnName("result_node_id");
            e.Property(u => u.CompletedAt).HasColumnName("completed_at");
            e.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.UserId);
            e.HasIndex(u => u.ExpiresAt);
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.At).HasColumnName("at");
            e.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
            e.Property(a => a.ActorKind).HasColumnName("actor_kind").HasConversion(LowercaseEnum<AuditActorKind>());
            e.Property(a => a.Ip).HasColumnName("ip");
            e.Property(a => a.UserAgent).HasColumnName("user_agent");
            e.Property(a => a.Action).HasColumnName("action");
            e.Property(a => a.TargetNodeId).HasColumnName("target_node_id");
            e.Property(a => a.TargetVersionId).HasColumnName("target_version_id");
            e.Property(a => a.Details).HasColumnName("details").HasColumnType("jsonb");
            e.HasIndex(a => a.At);
            e.HasIndex(a => new { a.TargetNodeId, a.At });
        });
    }

    private static ValueConverter<T, string> LowercaseEnum<T>() where T : struct, Enum =>
        new(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<T>(v, ignoreCase: true));
}
