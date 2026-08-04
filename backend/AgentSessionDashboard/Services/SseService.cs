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

    public void BroadcastQuestionUpdate(InboxQuestion question)
    {
        var payload = JsonSerializer.Serialize(QuestionPayload(question), _jsonOptions);
        Broadcast($"event: question-update\ndata: {payload}\n\n");
    }

    public void BroadcastJobRun(JobRun run)
    {
        var payload = JsonSerializer.Serialize(new
        {
            run.Id,
            run.JobName,
            run.StartedAt,
            run.FinishedAt,
            run.Outcome,
            run.Detail,
            run.Created,
            run.Applied
        }, _jsonOptions);
        Broadcast($"event: job-run\ndata: {payload}\n\n");
    }

    /// <summary>
    /// Shape sent to the client. JSON columns are re-parsed so the frontend gets
    /// real arrays/objects rather than strings it has to parse itself. Shared with
    /// the REST endpoints so SSE and GET never drift.
    /// </summary>
    public static object QuestionPayload(InboxQuestion q) => new
    {
        q.Id,
        q.Fingerprint,
        q.Source,
        q.SubjectId,
        q.DetectionType,
        q.DetectionKey,
        q.Title,
        q.Body,
        Options = QuestionService.ReadJson(q.Options),
        Recommendation = QuestionService.ReadJson(q.Recommendation),
        PinnedDelete = QuestionService.ReadJson(q.PinnedDelete),
        q.SubjectHash,
        q.State,
        q.AnswerKey,
        q.AnswerText,
        q.AnsweredAt,
        q.AppliedAt,
        q.DismissedAt,
        AppliedOps = QuestionService.ReadJson(q.AppliedOps),
        History = QuestionService.ReadJson(q.History),
        q.Note,
        q.CreatedAt
    };

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
