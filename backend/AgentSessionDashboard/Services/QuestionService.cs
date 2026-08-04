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

    /// <summary>
    /// How long a dismissal silences a finding before it may be raised again.
    ///
    /// Dismissing is not answering: it closes the card and resolves nothing, so a
    /// permanent mute means the question gets exhausted while the problem stays put.
    /// The window is the whole mechanism — short enough that a real blocker resurfaces,
    /// long enough that "not now" is respected rather than re-asked every half hour.
    /// </summary>
    public static readonly TimeSpan DismissalCoolOff = TimeSpan.FromDays(7);

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
        var fingerprint = BuildFingerprint(req.Source, req.SubjectId, req.DetectionType, req.DetectionKey);
        var existing = await db.Questions.FirstOrDefaultAsync(q => q.Fingerprint == fingerprint);

        // Transitional, and self-limiting: questions written before detectionKey existed
        // carry the three-part fingerprint, so the first keyed ask about one of them would
        // otherwise miss and insert a duplicate. Every already-settled question would
        // resurface once — losing its history, and skipping the dismissal cool-off, which
        // is precisely the guarantee the cool-off was added to provide. Adopt the keyless
        // row instead. Safe against the unique index: the keyed fingerprint has just been
        // shown not to exist.
        if (existing is null && !string.IsNullOrWhiteSpace(req.DetectionKey))
        {
            var legacyFingerprint = BuildFingerprint(req.Source, req.SubjectId, req.DetectionType);
            var legacy = await db.Questions.FirstOrDefaultAsync(
                q => q.Fingerprint == legacyFingerprint && q.DetectionKey == null);
            if (legacy is not null)
            {
                legacy.Fingerprint = fingerprint;
                legacy.DetectionKey = req.DetectionKey;
                // Saved here, not left to the branches below: the identity upgrade has to
                // stick whatever disposition follows, and `suppressed` returns without
                // saving. Otherwise the row keeps its keyless fingerprint, gets adopted
                // again by the *next* finding, and no second question can ever be created.
                await db.SaveChangesAsync();
                existing = legacy;
            }
        }

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
                DetectionKey = req.DetectionKey,
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

        // Settled. Two different reasons to raise it again, and they are not equivalent.
        var changed = !string.IsNullOrEmpty(req.SubjectHash)
                      && !string.Equals(req.SubjectHash, existing.SubjectHash, StringComparison.Ordinal);

        // An `applied` row settled the problem; only a changed subject unsettles it.
        // A `dismissed` row settled nothing — it closed the card — so the finding
        // still being detected after the cooling-off window is reason enough.
        var dismissalExpired = existing.State == QuestionStates.Dismissed
                               && DateTime.UtcNow - (existing.DismissedAt ?? existing.AnsweredAt ?? existing.CreatedAt)
                                  > DismissalCoolOff;

        if (!changed && !dismissalExpired)
            return new UpsertResult(existing, "suppressed");

        // A changed subject jumps the cap, for the reason reopens always have: a full
        // pool must not be able to hide a subject that moved. An expired dismissal has
        // no such claim — you already said "not now" — so it waits for a free slot
        // rather than making the inbox four deep.
        if (!changed && await CountOpenAsync() >= OpenQuestionCap)
            return new UpsertResult(existing, "at-cap");

        var reason = changed
            ? "subject changed since this was settled"
            : "dismissal expired and the finding is still present";

        PushHistory(existing, reason);
        ApplyContent(existing, req);
        existing.State = QuestionStates.Open;
        existing.AnswerKey = null;
        existing.AnswerText = null;
        existing.AnsweredAt = null;
        existing.AppliedAt = null;
        existing.AppliedOps = null;
        existing.DismissedAt = null;
        existing.Note = changed
            ? "Re-asked: the subject changed after this was settled."
            : "Re-asked: you dismissed this a while ago and it is still outstanding.";
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
        q.DismissedAt = DateTime.UtcNow;
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

    /// <summary>
    /// Identity of a finding. The key is appended only when supplied, so questions
    /// written before it existed keep matching their own three-part fingerprint —
    /// no migration, and nothing already settled resurfaces as a duplicate.
    /// </summary>
    public static string BuildFingerprint(
        string source, string? subjectId, string detectionType, string? detectionKey = null)
    {
        var head = $"{source}|{subjectId ?? "-"}|{detectionType}";
        return string.IsNullOrWhiteSpace(detectionKey) ? head : $"{head}|{detectionKey.Trim()}";
    }

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
        // Rewritten for completeness only — this row was found *by* fingerprint, so
        // these already match by construction. A different key is a different row.
        q.DetectionKey = req.DetectionKey;
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
    string? DetectionKey,
    string Title,
    string Body,
    JsonNode? Options,
    JsonNode? Recommendation,
    JsonNode? PinnedDelete,
    string? SubjectHash);
