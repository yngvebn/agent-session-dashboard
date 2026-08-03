using System.ComponentModel.DataAnnotations;

namespace AgentSessionDashboard.Models;

/// <summary>
/// Heartbeat for an unattended job. Exists so silent death is visible: without
/// it, an inbox that stopped filling three weeks ago reads exactly like
/// "nothing to decide".
/// </summary>
public class JobRun
{
    [Key]
    public int Id { get; set; }

    public string JobName { get; set; } = "";

    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary><c>ok | skipped | failed</c>. <c>skipped</c> is a healthy no-op.</summary>
    public string? Outcome { get; set; }

    public string? Detail { get; set; }

    public int Created { get; set; }
    public int Applied { get; set; }
}

public static class JobOutcomes
{
    public const string Ok = "ok";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}
