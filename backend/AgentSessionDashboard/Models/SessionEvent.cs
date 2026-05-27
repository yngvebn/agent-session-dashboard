using System.ComponentModel.DataAnnotations;

namespace AgentSessionDashboard.Models;

public class SessionEvent
{
    [Key]
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = "";
}
