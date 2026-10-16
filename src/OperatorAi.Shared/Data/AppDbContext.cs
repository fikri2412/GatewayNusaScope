using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace OperatorAi.Shared.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<SavedAnswer> SavedAnswers => Set<SavedAnswer>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ArtifactVersion> ArtifactVersions => Set<ArtifactVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        const string now = "SYSUTCDATETIME()";

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.ModelAlias).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Username).HasMaxLength(50);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.SecurityStamp).HasDefaultValueSql("NEWID()");
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            // Collation database tidak peka huruf besar/kecil: "Budi" dan "budi" bentrok di sini.
            e.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_users_username");
            // Email unik hanya bila diisi; akun lama tanpa email tidak bentrok satu sama lain.
            e.HasIndex(x => x.Email).IsUnique().HasFilter("[email] IS NOT NULL").HasDatabaseName("ux_users_email");
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.FinishReason).HasMaxLength(30);
            e.Property(x => x.ModelAlias).HasMaxLength(100);
            // Sentinel = true (sama dengan default kolom) supaya nilai 0 jawaban alternatif tetap terkirim saat insert.
            e.Property(x => x.IsPrimary).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ConversationId, x.Id }).HasDatabaseName("ix_messages_conversation");
        });

        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.StoragePath).HasMaxLength(500);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<QueueItem>(e =>
        {
            e.ToTable("queue_items", t => t.HasCheckConstraint("ck_queue_items_execution_policy",
                "[status] NOT IN ('Pending','Processing') OR ([source_message_id] IS NOT NULL AND [source_message_id] > 0 AND [execution_policy_json] IS NOT NULL AND ISJSON([execution_policy_json]) = 1 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.maxRetries')), 0) BETWEEN 1 AND 20 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.staleLockMinutes')), 0) BETWEEN 1 AND 1440)"));
            e.ToTable(t => t.HasCheckConstraint("ck_queue_items_model_snapshot",
                "[status] NOT IN ('Pending','Processing') OR (ISJSON([payload_json]) = 1 AND JSON_VALUE([payload_json], '$.model') IS NOT NULL AND LEN(JSON_VALUE([payload_json], '$.model')) BETWEEN 1 AND 100)"));
            e.ToTable(t => t.HasCheckConstraint("ck_queue_items_runtime_limits",
                "[status] NOT IN ('Pending','Processing') OR (ISJSON(JSON_QUERY([execution_policy_json], '$.ai')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.upload')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.document')) = 1 AND JSON_VALUE([execution_policy_json], '$.ai.attemptTimeoutSeconds') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.upload.maxAttachmentsPerMessage') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.document.watchdogSeconds') IS NOT NULL)"));
            e.Property(x => x.Type).HasMaxLength(30);
            e.Property(x => x.ClientRequestId).HasMaxLength(80);
            e.Property(x => x.IsPrimary).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.LockedBy).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RetryCount).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.Status, x.Id }).HasDatabaseName("ix_queue_items_status");
            // Guard per percakapan pada klaim (DESIGN bagian 6) memindai item satu percakapan.
            e.HasIndex(x => new { x.ConversationId, x.Status }).HasDatabaseName("ix_queue_items_conversation");
            // Idempotensi enqueue (§4.3): unik per percakapan hanya saat client_request_id diisi.
            e.HasIndex(x => new { x.ConversationId, x.ClientRequestId }).IsUnique()
                .HasFilter("[client_request_id] IS NOT NULL").HasDatabaseName("ux_queue_items_client_request");
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("jobs", t => t.HasCheckConstraint("ck_jobs_execution_policy",
                "[status] NOT IN ('Pending','Processing') OR ([source_message_id] IS NOT NULL AND [source_message_id] > 0 AND [execution_policy_json] IS NOT NULL AND ISJSON([execution_policy_json]) = 1 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.maxRetries')), 0) BETWEEN 1 AND 20 AND ISNULL(TRY_CONVERT(int, JSON_VALUE([execution_policy_json], '$.staleLockMinutes')), 0) BETWEEN 1 AND 1440)"));
            e.ToTable(t => t.HasCheckConstraint("ck_jobs_model_snapshot",
                "[status] NOT IN ('Pending','Processing') OR (ISJSON([input_json]) = 1 AND JSON_VALUE([input_json], '$.model') IS NOT NULL AND LEN(JSON_VALUE([input_json], '$.model')) BETWEEN 1 AND 100)"));
            e.ToTable(t => t.HasCheckConstraint("ck_jobs_runtime_limits",
                "[status] NOT IN ('Pending','Processing') OR (ISJSON(JSON_QUERY([execution_policy_json], '$.ai')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.upload')) = 1 AND ISJSON(JSON_QUERY([execution_policy_json], '$.document')) = 1 AND JSON_VALUE([execution_policy_json], '$.ai.attemptTimeoutSeconds') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.upload.maxAttachmentsPerMessage') IS NOT NULL AND JSON_VALUE([execution_policy_json], '$.document.watchdogSeconds') IS NOT NULL)"));
            e.Property(x => x.JobType).HasMaxLength(30);
            e.Property(x => x.IsPrimary).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.LockedBy).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RetryCount).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.DependsOnJobId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.Status, x.JobType, x.Id }).HasDatabaseName("ix_jobs_status_type");
            e.HasIndex(x => x.ConversationId).HasDatabaseName("ix_jobs_conversation");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_refresh_tokens_user_id");
        });

        b.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_password_reset_tokens_token_hash");
            e.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_password_reset_tokens_user_id_created_at");
        });

        b.Entity<SavedAnswer>(e =>
        {
            e.ToTable("saved_answers");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.SessionTitle).HasMaxLength(200);
            e.Property(x => x.ModelAlias).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            // message_id & conversation_id sengaja tanpa FK: snapshot tetap ada setelah sesi dihapus (§4.4).
            e.HasIndex(x => new { x.UserId, x.MessageId }).IsUnique()
                .HasFilter("[message_id] IS NOT NULL").HasDatabaseName("ux_saved_answers_user_message");
            e.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_saved_answers_user_created");
        });

        b.Entity<Artifact>(e =>
        {
            e.ToTable("artifacts");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(40);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt }).HasDatabaseName("ix_artifacts_conversation_created");
        });

        b.Entity<ArtifactVersion>(e =>
        {
            e.ToTable("artifact_versions");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Source).HasMaxLength(10);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Artifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ArtifactId, x.Version }).IsUnique().HasDatabaseName("ux_artifact_versions_artifact_version");
        });

        // Kolom snake_case: ConversationId -> conversation_id.
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
            p.SetColumnName(Regex.Replace(p.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
    }
}
