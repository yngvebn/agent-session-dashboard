using System.ComponentModel.DataAnnotations;

namespace AgentSessionDashboard.Models;

/// <summary>
/// A decision the dashboard is waiting on. Written by the decision-inbox job,
/// answered by the human on the startpage, then applied by a later run of the
/// same job. See docs/DECISION-INBOX.md.
///
/// The store is deliberately domain-agnostic: it knows nothing about Linear.
/// <see cref="SubjectId"/> is an opaque string and the operations an answer
/// implies are derived outside this process.
/// </summary>
public class InboxQuestion
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Identity of the question:
    /// <c>source|subjectId|detectionType[|detectionKey]</c>. Unique. A second ask
    /// with the same fingerprint updates or reopens this row rather than inserting
    /// a duplicate — that is what makes the duplicate-flood failure mode
    /// structurally impossible rather than merely unlikely.
    ///
    /// The key is appended only when present, so a producer that omits it keeps
    /// the original three-part identity and needs no migration.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>e.g. <c>ret-backlog</c> or <c>hygiene</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>e.g. <c>RET-381</c>. Null for findings that have no subject.</summary>
    public string? SubjectId { get; set; }

    /// <summary>
    /// What kind of detection produced this: <c>options-menu</c>,
    /// <c>unpinned-migration</c>, <c>epic-no-children</c>,
    /// <c>gating-mismatch</c>, <c>fleet-blocked</c>, <c>wildcard</c>.
    /// </summary>
    public string DetectionType { get; set; } = "";

    /// <summary>
    /// Short stable slug for the *specific finding* — <c>process-managers-table</c>,
    /// not <c>RET-79</c> and not <c>unpinned-migration</c>. Part of the fingerprint,
    /// so it is what lets one subject carry two questions of the same detection type
    /// instead of the second silently replacing the first.
    ///
    /// Must be derived from something stable in the finding: a key that drifts
    /// between runs re-asks a question that was already answered.
    /// </summary>
    public string? DetectionKey { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Markdown context rendered on the card.</summary>
    public string Body { get; set; } = "";

    /// <summary>JSON array of <c>{ key, label, description }</c>.</summary>
    public string Options { get; set; } = "[]";

    /// <summary>
    /// JSON <c>{ key, rationale, confidence }</c> — which option the producer
    /// would pick and why, so the human is calibrating a proposal rather than
    /// starting cold, and can see which guardrail produced a pick they disagree
    /// with.
    ///
    /// Advisory only. The applier derives operations from <see cref="AnswerKey"/>
    /// alone and never reads this; if it ever fell back to the recommendation,
    /// one-hop apply would become no-hop apply.
    /// </summary>
    public string? Recommendation { get; set; }

    /// <summary>
    /// JSON <c>{ heading, contentHash }</c> — the one description section an
    /// answer is permitted to delete, pinned when the question was written.
    /// The applier bails if the hash has moved.
    /// </summary>
    public string? PinnedDelete { get; set; }

    /// <summary>
    /// Baseline hash of the subject text this question was derived from. A
    /// changed hash means the subject was materially edited and the question
    /// should be re-asked; the applier rebaselines this after its own writes so
    /// it never re-triggers on its own edit.
    /// </summary>
    public string? SubjectHash { get; set; }

    /// <summary><c>open | answered | applied | dismissed | failed</c>.</summary>
    public string State { get; set; } = QuestionStates.Open;

    public string? AnswerKey { get; set; }

    /// <summary>Optional free text supplied alongside the chosen option.</summary>
    public string? AnswerText { get; set; }

    public DateTime? AnsweredAt { get; set; }
    public DateTime? AppliedAt { get; set; }

    /// <summary>
    /// When the question was dismissed. Tracked separately from
    /// <see cref="AnsweredAt"/> because it means something different — an answer
    /// resolves the underlying problem, a dismissal only closes the card — and
    /// because the cooling-off window that lets a dismissed finding come back is
    /// measured from here.
    /// </summary>
    public DateTime? DismissedAt { get; set; }

    /// <summary>JSON record of the operations that actually ran.</summary>
    public string? AppliedOps { get; set; }

    /// <summary>JSON array of superseded answers — the re-ask trail.</summary>
    public string? History { get; set; }

    /// <summary>Why the last apply failed, or why the question was reopened.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class QuestionStates
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string Applied = "applied";
    public const string Dismissed = "dismissed";
    public const string Failed = "failed";
}
