using System.ComponentModel.DataAnnotations;

namespace AgentSessionDashboard.Models;

/// <summary>
/// A request to run a job now, rather than waiting for its next cadence window.
/// Written by the dashboard when someone presses the button, claimed by the job's
/// own runner on the host — the container cannot start a host process, so the
/// store is the only channel the two share.
///
/// One row per press rather than a flag, which costs nothing and leaves an audit
/// trail of manual triggers.
/// </summary>
public class JobRequest
{
    [Key]
    public int Id { get; set; }

    public string JobName { get; set; } = "";

    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// When the runner claimed this request. Set at the *start* of the run it
    /// triggers, not the end: clearing on completion would swallow a request that
    /// arrived while that run was already in flight, so the press would vanish.
    /// </summary>
    public DateTime? ConsumedAt { get; set; }
}
