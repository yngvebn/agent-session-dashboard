using AgentSessionDashboard.Data;
using AgentSessionDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentSessionDashboard.Services;

/// <summary>
/// Heartbeat and trigger store for unattended jobs. Kept trivially simple: the job
/// records one row per run, the UI reads the latest to decide whether to look
/// worried, and a request row lets the UI ask for a run without being able to
/// start one itself.
/// </summary>
public class JobRunService(SessionDbContext db, SseService sse)
{
    /// <summary>Runs older than this are pruned so the table cannot grow unbounded.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>
    /// How long an unfinished run is believed before it is treated as abandoned.
    ///
    /// This is a lease, not a status flag, and the distinction is load-bearing: a
    /// hard kill — scheduler limit, sleep, reboot, crash — leaves a row with no
    /// <see cref="JobRun.FinishedAt"/> forever. Gated on a boolean, the runner
    /// would then never run again and the inbox would look merely quiet, which is
    /// the exact failure the heartbeat exists to make visible.
    /// </summary>
    public static readonly TimeSpan RunLease = TimeSpan.FromMinutes(25);

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
        await PruneAsync();
        await db.SaveChangesAsync();
        sse.BroadcastJobRun(run);
        return run;
    }

    /// <summary>
    /// Opens an in-flight run: a row with no <see cref="JobRun.FinishedAt"/>. That
    /// absence *is* the "running" state — the UI reads it to disable the button and
    /// the runner reads it to avoid overlapping itself. No separate status field, so
    /// there is nothing that can disagree with the heartbeat.
    /// </summary>
    public async Task<JobRun> StartAsync(string jobName)
    {
        var run = new JobRun { JobName = jobName, StartedAt = DateTime.UtcNow };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync();
        sse.BroadcastJobRun(run);
        return run;
    }

    /// <summary>Closes an in-flight run. Returns null if the id is unknown.</summary>
    public async Task<JobRun?> FinishAsync(int id, RecordJobRunRequest req)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return null;

        run.FinishedAt = DateTime.UtcNow;
        run.Outcome = req.Outcome;
        run.Detail = Truncate(req.Detail, 4000);
        run.Created = req.Created ?? 0;
        run.Applied = req.Applied ?? 0;
        await PruneAsync();
        await db.SaveChangesAsync();
        sse.BroadcastJobRun(run);
        return run;
    }

    public async Task<List<JobRequest>> GetPendingRequestsAsync(string jobName) =>
        await db.JobRequests
            .Where(r => r.JobName == jobName && r.ConsumedAt == null)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();

    public async Task<JobRequest> RequestAsync(string jobName)
    {
        var request = new JobRequest { JobName = jobName, RequestedAt = DateTime.UtcNow };
        db.JobRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>
    /// Claims every pending request for the job, returning how many there were.
    /// Claiming all of them collapses a flurry of presses into the single run they
    /// were all asking for, rather than queueing one run each.
    /// </summary>
    public async Task<int> ClaimRequestsAsync(string jobName)
    {
        var pending = await GetPendingRequestsAsync(jobName);
        if (pending.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var request in pending) request.ConsumedAt = now;
        await db.SaveChangesAsync();
        return pending.Count;
    }

    private async Task PruneAsync()
    {
        var cutoff = DateTime.UtcNow - Retention;
        var stale = await db.JobRuns.Where(r => r.StartedAt < cutoff).ToListAsync();
        if (stale.Count > 0) db.JobRuns.RemoveRange(stale);

        var staleRequests = await db.JobRequests.Where(r => r.RequestedAt < cutoff).ToListAsync();
        if (staleRequests.Count > 0) db.JobRequests.RemoveRange(staleRequests);
    }

    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;
}

public record StartJobRunRequest(string JobName);

public record RecordJobRunRequest(
    string JobName,
    string Outcome,
    string? Detail,
    DateTime? StartedAt,
    int? Created,
    int? Applied);
