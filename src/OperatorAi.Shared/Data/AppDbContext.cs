using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace OperatorAi.Shared.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AiCall> AiCalls => Set<AiCall>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        const string now = "SYSUTCDATETIME()";

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.Property(x => x.Role).HasMaxLength(20);
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
            e.ToTable("queue_items");
            e.Property(x => x.Type).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RetryCount).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.Status, x.Id }).HasDatabaseName("ix_queue_items_status");
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.Property(x => x.JobType).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RetryCount).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.DependsOnJobId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.Status, x.JobType, x.Id }).HasDatabaseName("ix_jobs_status_type");
            e.HasIndex(x => x.ConversationId).HasDatabaseName("ix_jobs_conversation");
        });

        b.Entity<AiCall>(e =>
        {
            e.ToTable("ai_calls");
            e.Property(x => x.Source).HasMaxLength(30);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.StopReason).HasMaxLength(30);
            e.Property(x => x.ToolsCalled).HasMaxLength(400);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(now);
        });

        // Kolom snake_case: ConversationId -> conversation_id.
        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
            p.SetColumnName(Regex.Replace(p.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
    }
}
