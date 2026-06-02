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
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InactiveThreshold = TimeSpan.FromHours(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string? _copilotSessionsPath =
        Environment.GetEnvironmentVariable("COPILOT_SESSIONS_PATH");

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
                else if (session.Status == "idle" && HasDefinitelyEnded(session))
                {
                    session.Status = "closed";
                    logger.LogDebug("Session {SessionId} → closed (lock file gone).", session.SessionId);
                    changed.Add(session);
                }
                else if (session.Status == "idle" && age > InactiveThreshold)
                {
                    session.Status = "crashed";
                    logger.LogDebug("Session {SessionId} → inactive (idle for {Age:F1}h).", session.SessionId, age.TotalHours);
                    changed.Add(session);
                }
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

    private bool IsSessionRunning(AgentSessionDashboard.Models.Session session)
    {
        // On Linux with pid:host, the PID check works directly.
        if (IsPidRunning(session.Pid)) return true;

        // For Copilot sessions (macOS Docker Desktop can't see host PIDs),
        // fall back to checking the inuse lock file in the mounted session state folder.
        if (!string.IsNullOrEmpty(_copilotSessionsPath))
        {
            var sessionDir = Path.Combine(_copilotSessionsPath, session.SessionId);
            if (Directory.Exists(sessionDir))
            {
                // Lock file present → process is alive. No lock file → process is gone.
                return Directory.GetFiles(sessionDir, "inuse.*.lock").Length > 0;
            }
        }

        // No Copilot session state folder for this session (e.g. Claude Code) and PID is gone.
        return false;
    }

    /// <summary>
    /// Returns true only when we can reliably confirm the session process has ended.
    /// Uses the Copilot inuse lock file — if the session directory exists but has no lock file,
    /// the process is definitively gone. Falls back to PID check (works on Linux with pid:host).
    /// Returns false (don't close) when the session type cannot be reliably checked.
    /// </summary>
    private bool HasDefinitelyEnded(AgentSessionDashboard.Models.Session session)
    {
        // Check Copilot lock file first — this is the reliable path for Copilot sessions
        // running on macOS where Docker Desktop cannot see host PIDs.
        if (!string.IsNullOrEmpty(_copilotSessionsPath))
        {
            var sessionDir = Path.Combine(_copilotSessionsPath, session.SessionId);
            if (Directory.Exists(sessionDir))
            {
                // Session dir exists but no lock file → process is definitively gone
                return Directory.GetFiles(sessionDir, "inuse.*.lock").Length == 0;
            }
        }

        // For non-Copilot sessions (e.g. Claude Code), only close if PID is confirmed gone.
        // On macOS Docker Desktop this will be unreliable (can't see host PIDs),
        // so we only close if IsPidRunning returns false AND the PID is positive (was ever set).
        if (session.Pid > 0 && !IsPidRunning(session.Pid)) return true;

        // Cannot reliably determine — leave as idle.
        return false;
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
