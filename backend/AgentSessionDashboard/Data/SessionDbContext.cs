using AgentSessionDashboard.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSessionDashboard.Data;

public class SessionDbContext(DbContextOptions<SessionDbContext> options) : DbContext(options)
{
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Shortcut> Shortcuts => Set<Shortcut>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<InboxQuestion> Questions => Set<InboxQuestion>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(s => s.SessionId);
            entity.Property(s => s.SessionId).IsRequired();
            entity.Property(s => s.StartedAt).HasConversion(
                v => v.ToUniversalTime(),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            entity.Property(s => s.LastSeen).HasConversion(
                v => v.ToUniversalTime(),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });

        modelBuilder.Entity<InboxQuestion>(entity =>
        {
            entity.ToTable("Questions");
            entity.HasIndex(q => q.Fingerprint).IsUnique();
            entity.Property(q => q.CreatedAt).HasConversion(UtcDate);
            entity.Property(q => q.AnsweredAt).HasConversion(NullableUtcDate);
            entity.Property(q => q.AppliedAt).HasConversion(NullableUtcDate);
        });

        modelBuilder.Entity<JobRun>(entity =>
        {
            entity.ToTable("JobRuns");
            entity.HasIndex(r => new { r.JobName, r.StartedAt });
            entity.Property(r => r.StartedAt).HasConversion(UtcDate);
            entity.Property(r => r.FinishedAt).HasConversion(NullableUtcDate);
        });
    }

    // SQLite stores DateTime as TEXT with no offset, so reads come back as
    // Unspecified. Pin both directions to UTC, matching the Session mapping.
    private static readonly ValueConverter<DateTime, DateTime> UtcDate =
        new(v => v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcDate =
        new(v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
