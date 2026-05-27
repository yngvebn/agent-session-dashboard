using AgentSessionDashboard.Data;
using AgentSessionDashboard.Models;
using Microsoft.EntityFrameworkCore;



namespace AgentSessionDashboard.Services;

public class SessionService(SessionDbContext db, SseService sseService)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public async Task<List<Session>> GetActiveSessionsAsync()
    {
        var cutoff = DateTime.UtcNow - MaxAge;
        return await db.Sessions
            .Where(s => s.StartedAt >= cutoff)
            .OrderByDescending(s => s.LastSeen)
            .ToListAsync();
    }

    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        var session = await db.Sessions.FindAsync(sessionId);
        if (session == null) return false;
        db.Sessions.Remove(session);
        await db.SaveChangesAsync();
        sseService.BroadcastSessionClosed(sessionId);
        return true;
    }

    public async Task UpsertFromHookAsync(HookPayload payload)
    {
        var now = DateTime.UtcNow;
        var existing = await db.Sessions.FindAsync(payload.SessionId);

        switch (payload.EventType)
        {
            case "closed":
                if (existing != null)
                {
                    existing.Status = "closed";
                    existing.LastSeen = payload.Timestamp ?? now;
                    await db.SaveChangesAsync();
                    sseService.BroadcastSessionClosed(existing.SessionId);
                }
                return;

            case "worktree-create":
                if (existing != null && payload.WorktreePath != null)
                {
                    existing.Worktree = payload.WorktreePath;
                    existing.LastSeen = payload.Timestamp ?? now;
                    await db.SaveChangesAsync();
                    sseService.BroadcastSessionUpdate(existing);
                }
                return;

            case "worktree-remove":
                if (existing != null)
                {
                    existing.Worktree = null;
                    existing.LastSeen = payload.Timestamp ?? now;
                    await db.SaveChangesAsync();
                    sseService.BroadcastSessionUpdate(existing);
                }
                return;

            case "notification":
                if (existing != null && payload.Message != null)
                {
                    existing.LastActivity = payload.Message;
                    existing.LastSeen = payload.Timestamp ?? now;
                    if (existing.Status == "idle" || existing.Status == "crashed")
                        existing.Status = "running";

                    var ev = new SessionEvent
                    {
                        SessionId = existing.SessionId,
                        Timestamp = payload.Timestamp ?? now,
                        Message = payload.Message
                    };
                    db.SessionEvents.Add(ev);
                    await db.SaveChangesAsync();
                    sseService.BroadcastSessionUpdate(existing);
                    sseService.BroadcastSessionEvent(ev);
                }
                return;

            case "stop":
                if (existing != null && (payload.TokensIn > 0 || payload.TokensOut > 0))
                {
                    existing.TokensIn += payload.TokensIn;
                    existing.TokensOut += payload.TokensOut;
                    existing.LastSeen = payload.Timestamp ?? now;
                    await db.SaveChangesAsync();
                    sseService.BroadcastSessionUpdate(existing);
                }
                return;
        }

        // heartbeat / started / subagent-start — upsert
        if (existing == null)
        {
            existing = new Session
            {
                SessionId = payload.SessionId,
                Name = payload.Name ?? "",
                Status = "running",
                StartedAt = payload.Timestamp ?? now,
                LastSeen = payload.Timestamp ?? now,
                Pid = payload.Pid,
                WorkingDir = payload.WorkingDir ?? "",
                Branch = payload.Branch
            };
            db.Sessions.Add(existing);
        }
        else
        {
            existing.Name = payload.Name ?? existing.Name;
            existing.LastSeen = payload.Timestamp ?? now;
            existing.Pid = payload.Pid != 0 ? payload.Pid : existing.Pid;
            existing.WorkingDir = payload.WorkingDir ?? existing.WorkingDir;
            if (payload.Branch != null) existing.Branch = payload.Branch;
            if (existing.Status == "idle" || existing.Status == "crashed" ||
                (payload.EventType == "started" && existing.Status == "closed"))
            {
                existing.Status = "running";
                existing.LastActivity = null;
            }
        }

        await db.SaveChangesAsync();
        sseService.BroadcastSessionUpdate(existing);
    }
}

public class HookPayload
{
    public string SessionId { get; set; } = "";
    public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("event")]
    public string EventType { get; set; } = "heartbeat";
    public int Pid { get; set; }
    public string? WorkingDir { get; set; }
    public DateTime? Timestamp { get; set; }
    public string? Message { get; set; }
    public string? WorktreePath { get; set; }
    public string? Branch { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
}
