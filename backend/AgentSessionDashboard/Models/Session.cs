using System.ComponentModel.DataAnnotations;

namespace AgentSessionDashboard.Models;

public class Session
{
    [Key]
    public string SessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "running"; // running | idle | closed | crashed
    public DateTime StartedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public int Pid { get; set; }
    public string WorkingDir { get; set; } = "";
    public string? LastActivity { get; set; }
    public string? Worktree { get; set; }
    public string? Branch { get; set; }
}
