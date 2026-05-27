using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AgentSessionDashboard.Models;

namespace AgentSessionDashboard.Services;

public class SseService
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public (Guid clientId, ChannelReader<string> reader) AddClient()
    {
        var clientId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _clients[clientId] = channel;
        return (clientId, channel.Reader);
    }

    public void RemoveClient(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void BroadcastSessionUpdate(Session session)
    {
        var payload = JsonSerializer.Serialize(new
        {
            session.SessionId,
            session.Name,
            session.Status,
            session.LastSeen,
            session.StartedAt,
            session.Pid,
            session.WorkingDir,
            session.LastActivity,
            session.Worktree,
            session.Branch,
            session.TokensIn,
            session.TokensOut
        }, _jsonOptions);

        var message = $"event: session-update\ndata: {payload}\n\n";
        Broadcast(message);
    }

    public void BroadcastSessionEvent(SessionEvent ev)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ev.Id,
            ev.SessionId,
            ev.Timestamp,
            ev.Message
        }, _jsonOptions);
        var message = $"event: session-event\ndata: {payload}\n\n";
        Broadcast(message);
    }

    public void BroadcastSessionClosed(string sessionId)
    {
        var payload = JsonSerializer.Serialize(new { sessionId }, _jsonOptions);
        var message = $"event: session-closed\ndata: {payload}\n\n";
        Broadcast(message);
    }

    public void BroadcastKeepAlive()
    {
        Broadcast(": keepalive\n\n");
    }

    private void Broadcast(string message)
    {
        foreach (var (clientId, channel) in _clients)
        {
            if (!channel.Writer.TryWrite(message))
            {
                // Channel is full or completed — remove stale client
                RemoveClient(clientId);
            }
        }
    }

    public int ClientCount => _clients.Count;
}
