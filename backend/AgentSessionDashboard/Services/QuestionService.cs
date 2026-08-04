using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSessionDashboard.Data;
using AgentSessionDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentSessionDashboard.Services;

/// <summary>
/// Owns the question lifecycle. The rules here are the ones that keep the inbox
/// calm, so they live server-side rather than in the job's prompt — a prompt can
/// misbehave, a unique index cannot. See docs/DECISION-INBOX.md.
/// </summary>
public class QuestionService(SessionDbContext db, SseService sse)
{
    /// <summary>
    /// Hard ceiling on simultaneously-open questions. Above this the inbox stops
    /// being a coffee-break thing and becomes a second backlog.
    /// </summary>
    public const int OpenQuestionCap = 3;

    public async Task<List<InboxQuestion>> GetAsync(string? state)
    {
        var query = db.Questions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(state))
        {
            // Comma-separated so the job can ask for open+answered in one call.
            var wanted = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(q => wanted.Contains(q.State));
        }
        return await query.OrderBy(q => q.CreatedAt).ToListAsync();
    }

    public async Task<InboxQuestion?> GetByIdAsync(int id) =>
        await db.Questions.FirstOrDefaultAsync(q => q.Id == id);

    public async Task<int> CountOpenAsync() =>
        await db.Questions.CountAsync(q => q.State == QuestionStates.Open);

    public record UpsertResult(InboxQuestion? Question, string Disposition);

    /// <summary>
    /// Idempotent ask. Dispositions:
    /// <list type="bullet">
    /// <item><c>created</c> — new question.</item>
    /// <item><c>updated</c> — already open, content refreshed in place.</item>
    /// <item><c>reopened</c> — previously settled, but the subject changed materially.</item>
    /// <item><c>suppressed</c> — already settled and unchanged; nothing to do.</item>
    /// <item><c>pending-answer</c> — answered and awaiting apply; left untouched.</item>
    /// <item><c>at-cap</c> — refused, the open pool is full.</item>
    /// </list>
    /// </summary>
    public async Task<UpsertResult> UpsertAsync(UpsertQuestionRequest req)
    {
        var fingerprint = BuildFingerprint(req.Source, req.SubjectId, req.DetectionType);
        var existing = await db.Questions.FirstOrDefaultAsync(q => q.Fingerprint == fingerprint);

        if (existing is null)
        {
            // Cap applies to new questions only. A reopen is a re-ask of something
            // already engaged with, so it is allowed through even at the cap —
            // otherwise a full pool could permanently hide a changed subject.
            if (await CountOpenAsync() >= OpenQuestionCap)
                return new UpsertResult(null, "at-cap");

            var created = new InboxQuestion
            {
                Fingerprint = fingerprint,
                Source = req.Source,
                SubjectId = req.SubjectId,
                DetectionType = req.DetectionType,
                Title = req.Title,
                Body = req.Body,
                Options = Serialize(req.Options),
                Recommendation = req.Recommendation?.ToJsonString(),
                PinnedDelete = req.PinnedDelete?.ToJsonString(),
                SubjectHash = req.SubjectHash,
                State = QuestionStates.Open,
                CreatedAt = DateTime.UtcNow
            };
            db.Questions.Add(created);
            await db.SaveChangesAsync();
            sse.BroadcastQuestionUpdate(created);
            return new UpsertResult(created, "created");
        }

        // An answer waiting to be applied outranks a fresh ask — never clobber it.
        if (existing.State == QuestionStates.Answered)
            return new UpsertResult(existing, "pending-answer");

        if (existing.State == QuestionStates.Open || existing.State == QuestionStates.Failed)
        {
            ApplyContent(existing, req);
            existing.State = QuestionStates.Open;
            await db.SaveChangesAsync();
            sse.BroadcastQuestionUpdate(existing);
            return new UpsertResult(existing, "updated");
        }

        // Settled (applied / dismissed): only a materially changed subject reopens it.
        var changed = !string.IsNullOrEmpty(req.SubjectHash)
                      && !string.Equals(req.SubjectHash, existing.SubjectHash, StringComparison.Ordinal);
        if (!changed)
            return new UpsertResult(existing, "suppressed");

        PushHistory(existing, "subject changed since this was settled");
        ApplyContent(existing, req);
        existing.State = QuestionStates.Open;
        existing.AnswerKey = null;
        existing.AnswerText = null;
        existing.AnsweredAt = null;
        existing.AppliedAt = null;
        existing.AppliedOps = null;
        existing.Note = "Re-asked: the subject changed after this was settled.";
        await db.SaveChangesAsync();
        sse.BroadcastQuestionUpdate(existing);
        return new UpsertResult(existing, "reopened");
    }

    public async Task<InboxQuestion?> AnswerAsync(int id, string key, string? text)
    {
        var q = await GetByIdAsync(id);
        if (q is null) return null;

        q.AnswerKey = key;
        q.AnswerText = text;
        q.AnsweredAt = DateTime.UtcNow;
        q.State = QuestionStates.Answered;
        q.Note = null;
        await db.SaveChangesAsync();
        sse.BroadcastQuestionUpdate(q);
        return q;
    }

    public async Task<InboxQuestion?> DismissAsync(int id)
    {
        var q = await GetByIdAsync(id);
        if (q is null) return null;

        q.State = QuestionStates.Dismissed;
        q.AnsweredAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        sse.BroadcastQuestionUpdate(q);
        return q;
    }

    /// <summary>
    /// Records what actually ran and rebaselines <see cref="InboxQuestion.SubjectHash"/>
    /// to the post-write state. That rebaseline is what stops the job's own edit
    /// from looking like a material change and re-triggering the question it just
    /// answered.
    /// </summary>
    public async Task<InboxQuestion?> MarkAppliedAsync(int id, JsonNode? ops, string? subjectHash)
    {
        var q = await GetByIdAsync(id);
        if (q is null) return null;

        q.State = QuestionStates.Applied;
        q.AppliedAt = DateTime.UtcNow;
        q.AppliedOps = ops?.ToJsonString();
        if (!string.IsNullOrEmpty(subjectHash)) q.SubjectHash = subjectHash;
        q.Note = null;
        await db.SaveChangesAsync();
        sse.BroadcastQuestionUpdate(q);
        return q;
    }

    /// <summary>
    /// Pre-flight found the subject had moved. The answer is discarded and the
    /// question comes back with a note describing what changed.
    /// </summary>
    public async Task<InboxQuestion?> ReopenAsync(int id, string reason, string? subjectHash)
    {
        var q = await GetByIdAsync(id);
        if (q is null) return null;

        PushHistory(q, reason);
        q.State = QuestionStates.Open;
        q.AnswerKey = null;
        q.AnswerText = null;
        q.AnsweredAt = null;
        q.Note = reason;
        if (!string.IsNullOrEmpty(subjectHash)) q.SubjectHash = subjectHash;
        await db.SaveChangesAsync();
        sse.BroadcastQuestionUpdate(q);
        return q;
    }

    public async Task<InboxQuestion?> MarkFailedAsync(int id, string detail)
    {
        var q = await GetByIdAsync(id);
        if (q is null) return null;

        q.State = QuestionStates.Failed;
        q.Note = detail;
        await db.SaveChangesAsync();
        sse.BroadcastQuestionUpdate(q);
        return q;
    }

    public static string BuildFingerprint(string source, string? subjectId, string detectionType) =>
        $"{source}|{subjectId ?? "-"}|{detectionType}";

    private static void ApplyContent(InboxQuestion q, UpsertQuestionRequest req)
    {
        q.Title = req.Title;
        q.Body = req.Body;
        q.Options = Serialize(req.Options);
        q.Recommendation = req.Recommendation?.ToJsonString();
        q.PinnedDelete = req.PinnedDelete?.ToJsonString();
        q.SubjectHash = req.SubjectHash;
        q.Source = req.Source;
        q.SubjectId = req.SubjectId;
        q.DetectionType = req.DetectionType;
    }

    private static void PushHistory(InboxQuestion q, string reason)
    {
        var history = ParseArray(q.History);
        history.Add(new JsonObject
        {
            ["answerKey"] = q.AnswerKey,
            ["answerText"] = q.AnswerText,
            // Called before ApplyContent overwrites it, so this is the recommendation
            // as it stood when the superseded answer was given. A recommendation that
            // moves when the subject moves is the signal that what produced it is
            // worth a look.
            ["recommendedKey"] = RecommendedKey(q.Recommendation),
            ["answeredAt"] = q.AnsweredAt?.ToString("O"),
            ["appliedAt"] = q.AppliedAt?.ToString("O"),
            ["state"] = q.State,
            ["supersededAt"] = DateTime.UtcNow.ToString("O"),
            ["reason"] = reason
        });
        q.History = history.ToJsonString();
    }

    /// <summary>
    /// The option key the producer recommended, or null. Never throws: a
    /// malformed recommendation degrades to no recommendation rather than
    /// costing us the question it was attached to.
    /// </summary>
    private static string? RecommendedKey(string? recommendation) =>
        ReadJson(recommendation) is JsonObject obj && obj.TryGetPropertyValue("key", out var key)
            ? key?.ToString()
            : null;

    private static JsonArray ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonArray();
        try { return JsonNode.Parse(json) as JsonArray ?? new JsonArray(); }
        catch { return new JsonArray(); }
    }

    private static string Serialize(JsonNode? node) => node?.ToJsonString() ?? "[]";

    /// <summary>Parses a stored JSON column for API responses, never throwing.</summary>
    public static JsonNode? ReadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }
}

public record UpsertQuestionRequest(
    string Source,
    string? SubjectId,
    string DetectionType,
    string Title,
    string Body,
    JsonNode? Options,
    JsonNode? Recommendation,
    JsonNode? PinnedDelete,
    string? SubjectHash);
