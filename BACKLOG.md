# Medium Effort Backlog

## 1. Token / Cost Estimator

Show per-session token usage and estimated cost.

**Requires:** Claude Code hooks to emit token counts in the hook payload (or a new event type).  
**Backend:** Accumulate token counts on the `Session` model (`TokensIn`, `TokensOut`). Map to USD cost using model pricing.  
**Frontend:** Add a cost row to the session card. Possibly a running total in the header.

---

## 2. Session Grouping by Repo

Group session cards visually by project (`WorkingDir` / repo root).

**Backend:** Derive a `RepoName` from `WorkingDir` (last path segment, or parse `.git/config`).  
**Frontend:** Replace flat sessions grid with grouped sections — one per repo. Collapsible. Show repo name as section header with active count badge.

---

## 3. Last Activity Timeline

Show a mini feed of recent `LastActivity` messages per session, not just the latest one.

**Backend:** Add a `SessionEvent` table: `(SessionId, Timestamp, Message)`. Persist every `notification` hook event. Expose via `/api/sessions/{id}/events`.  
**Frontend:** Expandable panel on the session card showing last N events with timestamps. Auto-scroll to bottom on new events.

---

## 4. Decision Inbox — **built**, see [docs/DECISION-INBOX.md](docs/DECISION-INBOX.md)

Async decision queue so stalled backlog forks don't need a live Claude session to resolve.
A scheduled job asks up to 3 questions, you answer them on the startpage, and a later run
applies the answers to Linear.

**Backend:** `Questions` + `JobRuns` tables, `/api/questions*` and `/api/jobs/runs`, `question-update` / `job-run` SSE events.  
**Frontend:** `app-question-card` above the sessions grid, plus a heartbeat line that goes amber when the job stops running.  
**Job:** `scripts/run-decision-inbox.ps1`, registered by `scripts/register-decision-inbox-task.ps1`.

Follow-ups not built: snooze-for-N-days on dismiss, a second (non-RET) producer,
pruning of settled questions (they are retained deliberately — they *are* the
idempotency record).
