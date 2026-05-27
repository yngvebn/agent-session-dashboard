using System.Diagnostics;
using AgentSessionDashboard.Data;
using AgentSessionDashboard.Services;
using Microsoft.EntityFrameworkCore;

namespace AgentSessionDashboard.BackgroundServices;

public class SessionStatusWorker(
    IServiceScopeFactory scopeFactory,
    SseService sseService,
    ILogger<SessionStatusWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CrashedThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SessionStatusWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TickInterval, stoppingToken);
            await RunTickAsync(stoppingToken);
        }

        logger.LogInformation("SessionStatusWorker stopped.");
    }

    private async Task RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SessionDbContext>();

            var now = DateTime.UtcNow;
            var cutoff = now - MaxAge;

            // Purge old sessions
            var stale = await db.Sessions
                .Where(s => s.StartedAt < cutoff)
                .ToListAsync(cancellationToken);
            if (stale.Count > 0)
            {
                db.Sessions.RemoveRange(stale);
                logger.LogInformation("Purged {Count} stale session(s).", stale.Count);
            }

            // Derive status for active sessions
            var activeSessions = await db.Sessions
                .Where(s => s.Status == "running" || s.Status == "idle")
                .ToListAsync(cancellationToken);

            var changed = new List<AgentSessionDashboard.Models.Session>();

            foreach (var session in activeSessions)
            {
                var age = now - session.LastSeen;

                if (session.Status == "running" && age > IdleThreshold)
                {
                    session.Status = "idle";
                    logger.LogDebug("Session {SessionId} → idle (last seen {Age:F0}s ago).", session.SessionId, age.TotalSeconds);
                    changed.Add(session);
                }
                // Note: idle→crashed transition removed. PID check is unreliable when
                // the dashboard runs in Docker (host PIDs are invisible inside the container).
            }

            // Persist before broadcasting so clients always see saved state
            await db.SaveChangesAsync(cancellationToken);

            foreach (var session in changed)
            {
                sseService.BroadcastSessionUpdate(session);
            }

            // Send keepalive to SSE clients regardless of session changes
            sseService.BroadcastKeepAlive();
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress — exit gracefully
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in SessionStatusWorker tick.");
        }
    }

    private static bool IsPidRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
