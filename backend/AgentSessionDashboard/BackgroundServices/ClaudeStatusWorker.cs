using AgentSessionDashboard.Services;

namespace AgentSessionDashboard.BackgroundServices;

/// <summary>
/// Polls the Claude status Atom feed every five minutes. Statuspage updates on
/// the order of minutes, so anything tighter just burns their cache for nothing.
/// </summary>
public class ClaudeStatusWorker(
    ClaudeStatusService statusService,
    ILogger<ClaudeStatusWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ClaudeStatusWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await statusService.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed poll keeps the previous snapshot — better stale than gone.
                logger.LogWarning(ex, "Failed to refresh Claude status feed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("ClaudeStatusWorker stopped.");
    }
}
