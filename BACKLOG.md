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
