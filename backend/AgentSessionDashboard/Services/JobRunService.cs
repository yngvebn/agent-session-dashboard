using AgentSessionDashboard.Data;
using AgentSessionDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentSessionDashboard.Services;

/// <summary>
/// Heartbeat store for unattended jobs. Kept trivially simple: the job records
/// one row per run, the UI reads the latest to decide whether to look worried.
/// </summary>
public class JobRunService(SessionDbContext db, SseService sse)
{
    /// <summary>Runs older than this are pruned so the table cannot grow unbounded.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    public async Task<List<JobRun>> GetAsync(string? jobName, int limit)
    {
        var query = db.JobRuns.AsQueryable();
        if (!string.IsNullOrWhiteSpace(jobName))
            query = query.Where(r => r.JobName == jobName);

        return await query
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();
    }

    public async Task<JobRun> RecordAsync(RecordJobRunRequest req)
    {
        var run = new JobRun
        {
            JobName = req.JobName,
            StartedAt = req.StartedAt?.ToUniversalTime() ?? DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            Outcome = req.Outcome,
            Detail = Truncate(req.Detail, 4000),
            Created = req.Created ?? 0,
            Applied = req.Applied ?? 0
        };

        db.JobRuns.Add(run);

        var cutoff = DateTime.UtcNow - Retention;
        var stale = await db.JobRuns.Where(r => r.StartedAt < cutoff).ToListAsync();
        if (stale.Count > 0) db.JobRuns.RemoveRange(stale);

        await db.SaveChangesAsync();
        sse.BroadcastJobRun(run);
        return run;
    }

    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;
}

public record RecordJobRunRequest(
    string JobName,
    string Outcome,
    string? Detail,
    DateTime? StartedAt,
    int? Created,
    int? Applied);
