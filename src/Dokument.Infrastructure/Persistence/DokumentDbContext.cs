using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dokument.Infrastructure.Persistence;

public sealed class DokumentDbContext(DbContextOptions<DokumentDbContext> options)
    : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<BlobPreview> BlobPreviews => Set<BlobPreview>();
    public DbSet<Rendition> Renditions => Set<Rendition>();

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
            e.Property(u => u.QuotaUnlimited).HasColumnName("quota_unlimited");
            e.Property(u => u.QuotaSource)
                .HasColumnName("quota_source")
                .HasConversion(LowercaseEnum<QuotaSource>());
            e.Ignore(u => u.Quota);
            e.Property(u => u.UsedBytes).HasColumnName("used_bytes");
            e.Property(u => u.ReservedBytes).HasColumnName("reserved_bytes");
            e.Property(u => u.Locale)
                .HasColumnName("locale")
                .HasConversion(l => l!.Code, v => UiLanguage.TryParse(v));
            e.Property(u => u.ShowSeconds).HasColumnName("show_seconds");
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
            e.Property(s => s.Kind)
                .HasColumnName("kind")
                .HasConversion(LowercaseEnum<SessionKind>());
            e.Property(s => s.TokenHash).HasColumnName("token_hash");
            e.Property(s => s.DeviceName)
                .HasColumnName("device_name")
                .HasConversion(d => d!.Value, v => DeviceName.From(v));
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            e.Property(s => s.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.UserId);
            e.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureFiles(modelBuilder);
        ConfigureJobs(modelBuilder);
        ConfigurePreviews(modelBuilder);
    }

    private static readonly ValueConverter<ContentHash, byte[]> ContentHashConverter = new(
        h => h.ToArray(),
        v => ContentHash.FromBytes(v)
    );

    private static void ConfigureJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Id).HasColumnName("id");
            e.Property(j => j.Type)
                .HasColumnName("type")
                .HasConversion(t => t.Name, v => JobType.From(v));
            e.Property(j => j.Subject).HasColumnName("subject");
            e.Property(j => j.Status)
                .HasColumnName("status")
                .HasConversion(LowercaseEnum<JobStatus>());
            e.Property(j => j.Priority).HasColumnName("priority").HasConversion<int>();
            e.Property(j => j.Attempts).HasColumnName("attempts");
            e.Property(j => j.RunAfter).HasColumnName("run_after");
            e.Property(j => j.LockedBy).HasColumnName("locked_by");
            e.Property(j => j.LockedUntil).HasColumnName("locked_until");
            e.Property(j => j.LastError).HasColumnName("last_error");
            e.Property(j => j.CreatedAt).HasColumnName("created_at");
            e.Property(j => j.FinishedAt).HasColumnName("finished_at");
            e.Property(j => j.TraceParent).HasColumnName("trace_parent");

            // The claim query's index: what is due, best first. Finished jobs stay out of it.
            e.HasIndex(j => new
                {
                    j.Type,
                    j.Priority,
                    j.RunAfter,
                })
                .HasFilter("status IN ('queued', 'running')")
                .HasDatabaseName("ix_jobs_due");
            e.HasIndex(j => new { j.Type, j.Subject });
        });
    }

    private static void ConfigurePreviews(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BlobPreview>(e =>
        {
            e.ToTable("blob_previews");
            e.HasKey(p => p.BlobHash);
            e.Property(p => p.BlobHash)
                .HasColumnName("blob_hash")
                .HasConversion(ContentHashConverter);
            e.Property(p => p.Status)
                .HasColumnName("status")
                .HasConversion(LowercaseEnum<PreviewStatus>());
            e.Property(p => p.PipelineVersion).HasColumnName("pipeline_version");
            e.Property(p => p.Width).HasColumnName("width");
            e.Property(p => p.Height).HasColumnName("height");
            e.Property(p => p.PageCount).HasColumnName("page_count");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<Blob>()
                .WithOne()
                .HasForeignKey<BlobPreview>(p => p.BlobHash)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Rendition>(e =>
        {
            e.ToTable("renditions");
            e.HasKey(r => new { r.BlobHash, r.Kind });
            e.Property(r => r.BlobHash)
                .HasColumnName("blob_hash")
                .HasConversion(ContentHashConverter);
            e.Property(r => r.Kind)
                .HasColumnName("kind")
                .HasConversion(k => k.Key(), v => RenditionKinds.FromKey(v));
            e.Property(r => r.StorageKey)
                .HasColumnName("storage_key")
                .HasConversion(k => k.Value, v => DerivedKey.From(v));
            e.Property(r => r.MimeType).HasColumnName("mime_type");
            e.Property(r => r.Width).HasColumnName("width");
            e.Property(r => r.Height).HasColumnName("height");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.HasOne<Blob>()
                .WithMany()
                .HasForeignKey(r => r.BlobHash)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureFiles(ModelBuilder modelBuilder)
    {
        var nodeName = new ValueConverter<NodeName, string>(n => n.Value, v => NodeName.From(v));
        var contentHash = ContentHashConverter;

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
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(n => n.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Node>()
                .WithMany()
                .HasForeignKey(n => n.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.CurrentVersion)
                .WithOne()
                .HasForeignKey<Node>(n => n.CurrentVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            // Names are unique among the live children of a folder; the root (parent null) counts as one folder per owner.
            e.HasIndex(n => new
                {
                    n.OwnerId,
                    n.ParentId,
                    n.NameKey,
                })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("trashed_at IS NULL");
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
            e.HasOne<Node>()
                .WithMany()
                .HasForeignKey(v => v.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Blob>()
                .WithMany()
                .HasForeignKey(v => v.BlobHash)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(v => v.UploadedBy)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(v => new { v.NodeId, v.Number }).IsUnique();
            e.HasIndex(v => v.BlobHash);
        });

        modelBuilder.Entity<Blob>(e =>
        {
            e.ToTable("blobs");
            e.HasKey(b => b.Hash);
            e.Property(b => b.Hash).HasColumnName("hash").HasConversion(contentHash);
            e.Property(b => b.Size).HasColumnName("size");
            e.Property(b => b.StorageKey)
                .HasColumnName("storage_key")
                .HasConversion(k => k.Value, v => StorageKey.From(v));
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
            e.Property(u => u.ClaimedHash)
                .HasColumnName("claimed_hash")
                .HasConversion(contentHash!);
            e.Property(u => u.MimeType).HasColumnName("mime_type");
            e.Property(u => u.ClientModifiedAt).HasColumnName("client_modified_at");
            e.Property(u => u.OnConflict)
                .HasColumnName("on_conflict")
                .HasConversion(LowercaseEnum<NameConflictPolicy>());
            e.Property(u => u.CreatedAt).HasColumnName("created_at");
            e.Property(u => u.ExpiresAt).HasColumnName("expires_at");
            e.Property(u => u.ResultNodeId).HasColumnName("result_node_id");
            e.Property(u => u.CompletedAt).HasColumnName("completed_at");
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(u => u.UserId)
                .OnDelete(DeleteBehavior.Cascade);
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
            e.Property(a => a.ActorKind)
                .HasColumnName("actor_kind")
                .HasConversion(LowercaseEnum<AuditActorKind>());
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

    private static ValueConverter<T, string> LowercaseEnum<T>()
        where T : struct, Enum =>
        new(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<T>(v, ignoreCase: true));
}
