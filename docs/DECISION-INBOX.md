# Decision Inbox

Status: **built** 2026-08-03. Decisions below were settled in a grilling session the same
day; this document is both the spec and the record of why each fork went the way it did.
`BACKLOG.md` item 4 points here.

**As built:**

| Part | Where |
| --- | --- |
| Store + lifecycle rules | `backend/.../Services/QuestionService.cs`, `Models/InboxQuestion.cs` |
| Heartbeat | `backend/.../Services/JobRunService.cs`, `Models/JobRun.cs` |
| Endpoints + DDL | `backend/.../Program.cs` |
| Live delivery | `backend/.../Services/SseService.cs` (`question-update`, `job-run`) |
| Card UI | `frontend/src/app/question-card/`, `services/question.service.ts` |
| Job | `scripts/run-decision-inbox.ps1`, `scripts/decision-inbox-prompt.md` |
| Scheduling | `scripts/register-decision-inbox-task.ps1` |

## Problem

Every open decision in the retrospective.fun backlog needs the founder *sitting in a
Claude Code session* to resolve. As of 2026-08-03 that stalls six of the top backlog
items: `RET-381` (CI paths-filter policy), `RET-376` (which side of a tier-gate is
authoritative), `RET-433` (bundled task + investigation), plus two epics with no
children and one lockfile fix. `fleet-prep` can find all of these in about ninety
seconds — finding them was never the bottleneck. Answering them is.

The goal is therefore **not** to automate the analysis. It is to decouple decision
latency from session presence: turn each stalled fork into a 30-second answer on a
browser tab, so the `Todo` queue stays warm without a session being open.

## Shape

One scheduled job, two phases per run, talking HTTP to the existing dashboard.

```
Task Scheduler (hourly, as user)
   └── run-decision-inbox.ps1
         ├── cheap pre-flight: GET /api/questions  → skip if nothing to do
         └── claude -p  (cwd = C:\github\retrospective.fun)
               ├── PHASE A — drain: apply answered questions to Linear via linctl
               └── PHASE B — top up: ask up to the cap, write questions to dashboard
```

The dashboard renders open questions as cards on the startpage and posts answers back.
It knows nothing about Linear.

### Why the dashboard stays Linear-ignorant

Not a design preference — a forced constraint. The dashboard runs in a container
(`docker-compose.yml`, `5900:8080`, SQLite in a named volume). It cannot shell out to
`claude` or `linctl`. So the job is necessarily a host-side process talking over HTTP,
which makes the dashboard a generic question store plus UI whether or not that was the
intent. Useful side effect: the same inbox serves any project later.

### Why Task Scheduler and not the built-ins

Both built-in options were considered and each fails on a specific constraint:

| Option | Disqualifier |
| --- | --- |
| `CronCreate` | Session-only, in-memory. Dies when the Claude session exits; recurring jobs auto-expire after 7 days; fires only while the REPL is idle. An inbox that silently stopped filling is worse than none. |
| `/schedule` cloud routines | Genuinely persistent, but runs in a cloud container — no `localhost:5900`, no `~/.linctl-auth.json`. Cannot reach either half of this system. |

Task Scheduler wins by elimination: trigger on logon **plus** repeat every hour, "run
only when user is logged on", running **as the user account**. That last part matters —
see auth below.

### What the job inherits from the repo — and what it doesn't

Because the Claude session's cwd is the target repo, a lot of context arrives for free and
does **not** need restating in the prompt. Probed directly on 2026-08-03; the session
confirmed it already knows the tracker and CLI (`linctl`, team `RET`, never
`mcp__linear*`), that master is off-limits, and the project persona.

Two things it does *not* get, which is why the prompt is not merely a duplicate:

* **Skill bodies are not loaded.** The session sees each skill's name and description
  only — *"the body isn't loaded until the skill is invoked"*. So `fleet-prep`'s detection
  checklist and its `draft-merge` rule are absent by default. The prompt therefore
  **invokes** `fleet-prep` for detections rather than carrying a copy that would drift,
  and restates only the `draft-merge` rule, which is too important to depend on a skill
  invocation succeeding.
* **Constraints that *subtract*.** Nothing in the repo's context says "don't fix what you
  find" — rather the opposite. The typed-operation vocabulary is deliberately narrower
  than what `CLAUDE.md` permits, so it has to be spelled out.

### Auth — two separate credentials, both easy to get wrong

#### `CLAUDE_CONFIG_DIR` — the one that bit

`CLAUDE_CONFIG_DIR` is set **per-process in interactive shells and does not exist at User
or Machine scope.** A scheduled task inherits nothing and falls back to `~/.claude`, which
on this machine is a different config root with **an expired OAuth session and a
different memory set**. Manual runs passed because they inherited the variable; every
scheduled run would have failed on auth.

Both the wrapper and the registration script now take `-ConfigDir`, and the registration
script bakes the current value into the task's arguments rather than trusting the
environment. Verified 2026-08-03 with the variable stripped from the launching process:
the run authenticated and completed. Pointed deliberately at the unauthenticated root, it
records `failed` with `Claude auth failed (CLAUDE_CONFIG_DIR=…)` rather than a generic
parse error — auth expiry is the failure most likely to appear out of nowhere and the
least obvious from a generic message.

Note also that `Start-Job` spawns a fresh process, so the variable is set again inside the
job block; setting it in the parent alone is not enough.

#### `linctl` — verified working

`linctl` credentials live in `~/.linctl-auth.json` (a 67-byte per-user file), **not** an
environment variable. Verified 2026-08-03: `linctl auth status` authenticated
successfully from a fresh process with `LINCTL_API_KEY` unset in the Process, User and
Machine scopes. So an unattended process running as the user needs no credential setup.

Two consequences:

* The task **must** run as the user, not as `SYSTEM` — `SYSTEM` has a different profile
  and will not see that file.
* The session-start hook's `LINCTL_API_KEY not set` warning is a red herring here. It
  checks the container-bootstrap path, not the steady state.

## Settled decisions

Each of these was an explicit fork. Recorded with the reasoning so they can be revisited
deliberately rather than re-litigated by accident.

| # | Decision | Rationale |
| --- | --- | --- |
| 1 | **Task Scheduler + `claude -p`**, as user, hourly | Only option that is both local and unattended (see table above). |
| 2 | **One job, two phases** — not two jobs | One prompt, one schedule, one thing that can silently stop firing. Two writers on the same rows buys nothing. |
| 3 | **Producer: narrow + one wildcard slot** | Up to two mechanically-detected questions plus at most one open-ended judgement call per run. Bounds noise while leaving room to surface something unasked. |
| 4 | **Cap 3 open questions**, refill only as answered | Above this the inbox becomes a second backlog you also ignore. The founder-energy gate applies to the tool itself. |
| 5 | **Typed operations + audit comment** | Fixed vocabulary: set state, add/remove label, add `blocked-by` relation, append/remove a description section. Every write leaves a Linear comment citing the answer and its date. |
| 6 | **Surgical delete of the resolved options block**, archived to a comment first | A `Fix` section listing "Options: 1/2/3" is what makes a ticket un-fleetable. Deleting it is what makes the ticket unambiguous. Posting the removed text as a Linear comment *before* deleting means the reasoning moves to the comment thread rather than being destroyed. |
| 7 | **Consumer reasons at apply time, but the delete is pinned** | State, label and relation ops are derived at apply time. The delete target is captured by the producer at ask time (heading text + content hash) and the apply bails if that hash moved. Free rein where it is cheap; pinned at the one destructive op. |
| 8 | **Wildcard scope: backlog + repo/machine hygiene** | Lets it flag things no ticket covers — e.g. the 13 stale worktrees with nothing In Progress, a red nightly, lockfile drift. |
| 9 | **Re-ask when the ticket materially changes** | Fingerprint includes a hash of the relevant description section, so an edited `Fix` section resurfaces the question. |
| 10 | **Job records the post-write hash as the new baseline** | Closes a real bug in #6 + #9: the job's own delete changes the description, which would otherwise re-trigger the question it just answered — the duplicate-flood failure mode arriving by the front door. |
| 11 | **Hygiene answers acknowledge and close, logged only** | Findings that map to no Linear operation write nothing to Linear. Honest about the fact that you just wanted to know. |
| 12 | **Passive delivery via the existing SSE stream** | No notifications. The whole point is a calm coffee-break flow, and that notification path has already been tuned twice for noise (see `cae4beb`, `e356e9d`). |
| 13 | **May apply `fleet-safe`, but must re-earn it at apply time** | Fastest path from decision to running code. See the risk note below — this is the one decision with a tail worth understanding. Amended 2026-08-03: the label is never inherited from the answer, see below. |
| 14 | **One-hop apply** — no proposal/confirm step | Both writes (section delete, label/state change) are recoverable and fully audited, so a confirmation step buys little. |
| 15 | **Heartbeat row + stale indicator** | Every run records time and outcome; the card list shows a quiet "last checked Nh ago" that turns visibly stale past a threshold. Catches silent death — otherwise an empty inbox reads as "nothing to decide". |
| 16 | **Stale pre-flight discards the answer and re-asks with new context** | Correctness over forward progress. A half-applied answer is harder to reason about later than a re-answered one. |

### Risk note on decision 13

With `fleet-safe` writes plus one-hop apply, the full chain is:

```
cron writes question → one click on the startpage → next run reasons out the ops,
deletes the options block, sets Todo + fleet-safe → fleet wave claims it → agent
writes the code → create-pr.ps1 auto-merges → master auto-deploys to dev
```

One click on a browser tab to deployed code, with no other human in the path. This is a
deliberate choice for a single-operator machine, not an oversight.

### `fleet-safe` is re-earned, never inherited

The first build got this wrong, and it is worth recording why. Options were written
promising `fleet-safe` ("*sets state Todo and labels testing, Bug, fleet-safe*") and phase
A simply applied what the option said. Three problems with that:

1. **A question resolves one check; the label asserts five.** `fleet-prep` step 2 tests
   executable-not-container, unblocked, single decided path, no in-wave file collision,
   and the risk gate. A question usually settles only the third. Promising the label on
   that basis silently claims the other four — never verified. RET-433 would have been
   promoted without anyone noticing it collides with RET-422; both add e2e specs.
2. **The promise is stale by construction.** It is written when the question is asked and
   applied whenever the answer arrives, possibly days later. A `blocked-by` can appear; a
   sibling can be promoted into the same files.
3. **The promise can be wrong.** RET-21's option claimed that dropping the `Epic` *label*
   means "`HasChildren` is no longer the thing protecting it". `HasChildren` reads
   `children.nodes` and never looks at labels. Plausible-sounding and false — which is the
   general argument for the applier deriving operations from current state rather than
   trusting card text.

So phase A now re-runs the `fleet-prep` checks before applying `fleet-safe`, and
**withholds it** — promoting to `Todo` only — if any fail, recording
`fleet-safe withheld: <reason>` in the audit comment. Withholding is a normal outcome, not
a failure. Option descriptions must phrase the label as conditional; a card that promises
it outright is writing a cheque the applier is right to refuse.

**Required mitigation, no latency cost:** `fleet-prep` already codifies `draft-merge` for
tickets touching billing, payments, auth, tenant isolation or GDPR/erasure, and
`fleet-claim` honours it. Because this job may apply `fleet-safe`, it is **obliged** to
apply `draft-merge` alongside it for those same categories. That keeps the fast path fast
and removes the one tail outcome worth regretting — an unattended chain auto-merging
entitlement code. `RET-376` is exactly such a ticket.

## Data model

Follows the existing idiom in `Program.cs`: `EnsureCreated()` plus idempotent
`CREATE TABLE IF NOT EXISTS`, the same way `SessionEvents` and `Shortcuts` were added.
No migration framework. Roughly 15 lines of DDL.

```sql
CREATE TABLE IF NOT EXISTS "Questions" (
    "Id"            INTEGER NOT NULL CONSTRAINT "PK_Questions" PRIMARY KEY AUTOINCREMENT,
    "Fingerprint"   TEXT NOT NULL,   -- source|subjectId|detectionType — identity
    "Source"        TEXT NOT NULL,   -- 'ret-backlog' | 'hygiene'
    "SubjectId"     TEXT NULL,       -- 'RET-381'; NULL for hygiene findings
    "DetectionType" TEXT NOT NULL,   -- options-menu | unpinned-migration |
                                     -- epic-no-children | gating-mismatch | wildcard
    "Title"         TEXT NOT NULL,
    "Body"          TEXT NOT NULL,   -- markdown context shown on the card
    "Options"       TEXT NOT NULL,   -- JSON [{key,label,description}]
    "PinnedDelete"  TEXT NULL,       -- JSON {heading, contentHash} captured at ask time
    "SubjectHash"   TEXT NULL,       -- baseline hash of the relevant description section
    "State"         TEXT NOT NULL,   -- open | answered | applied | dismissed | failed
    "AnswerKey"     TEXT NULL,
    "AnswerText"    TEXT NULL,       -- optional free text alongside the chosen option
    "AnsweredAt"    TEXT NULL,
    "AppliedAt"     TEXT NULL,
    "AppliedOps"    TEXT NULL,       -- JSON record of what actually ran
    "History"       TEXT NULL,       -- JSON array of superseded answers (re-ask trail)
    "CreatedAt"     TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Questions_Fingerprint"
    ON "Questions" ("Fingerprint");

CREATE TABLE IF NOT EXISTS "JobRuns" (
    "Id"          INTEGER NOT NULL CONSTRAINT "PK_JobRuns" PRIMARY KEY AUTOINCREMENT,
    "JobName"     TEXT NOT NULL,
    "StartedAt"   TEXT NOT NULL,
    "FinishedAt"  TEXT NULL,
    "Outcome"     TEXT NULL,   -- ok | skipped | failed
    "Detail"      TEXT NULL,
    "Created"     INTEGER NOT NULL DEFAULT 0,
    "Applied"     INTEGER NOT NULL DEFAULT 0
);
```

`Fingerprint` is unique, so a re-ask (decision 9) **reopens the existing row** rather
than inserting a duplicate: the current answer is pushed onto `History`, the answer
fields clear, and `State` returns to `open`. That is what makes the duplicate-flood
failure mode structurally impossible rather than merely unlikely.

## API surface

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/questions?state=open` | Card list. Also the wrapper's cheap pre-flight. |
| `POST` | `/api/questions` | Upsert by fingerprint (producer). |
| `POST` | `/api/questions/{id}/answer` | `{ key, text? }` — from the card UI. |
| `POST` | `/api/questions/{id}/dismiss` | Closes without a Linear write. |
| `POST` | `/api/questions/{id}/applied` | `{ ops, subjectHash }` — records what ran and rebaselines the hash (decision 10). |
| `GET` | `/api/jobs/runs?name=decision-inbox&limit=1` | Heartbeat for the stale indicator. |
| `POST` | `/api/jobs/runs` | Job records its own run. |

Live delivery reuses the existing SSE machinery — `/api/sessions/stream` already
streams, so pushing question events to the startpage is nearly free.

## Wrapper script

`scripts/run-decision-inbox.ps1` in this repo. The cheap check lives **in the wrapper,
not in the prompt** — this is what makes an hourly trigger essentially free, since a
no-op run must not spend a token spinning up a session.

```
1. GET /api/questions
2. openCount   = questions where State = 'open'
   pendingApply = questions where State = 'answered'
3. if openCount >= 3 AND pendingApply = 0:
       POST /api/jobs/runs { Outcome = 'skipped' }
       exit 0                          # no LLM invoked
4. claude -p "<inbox prompt>"          # cwd = C:\github\retrospective.fun
5. POST /api/jobs/runs { Outcome, Detail, Created, Applied }
```

Step 3's condition matters: skipping on a full pool alone would strand answered
questions forever.

## Prompt contract

Phase A (drain) runs before phase B (top up), so answering a question frees a slot in
the same run.

**Phase A — apply answered questions.** For each `answered` question: re-read the
subject via `linctl issue get`; verify the ticket still matches `SubjectHash` and, where
a `PinnedDelete` exists, that its `contentHash` still matches. On mismatch, discard the
answer, reopen the question with a note describing what changed (decision 16). Otherwise
derive the typed operations, post the archive comment before any delete, apply, post the
audit comment, then `POST /applied` with the resulting hash.

**Phase B — top up to the cap.** Detect mechanically first — options menus in a `Fix`
section, unpinned DB changes, epics with `children=[]`, client/server gating mismatches
— then at most one wildcard question, which may cover repo or machine hygiene.

Hard constraints for the prompt to carry:

* Never exceed 3 open questions.
* Never start implementation. This job preps; `fleet`/`go` execute. The boundary is the
  same one `fleet-prep` draws.
* Never make a consequential product or architecture decision on the founder's behalf —
  surfacing the fork *is* the job.
* Apply `draft-merge` alongside `fleet-safe` for billing / auth / tenant-isolation /
  GDPR tickets (see the risk note above).
* Every Linear write leaves a comment citing the answer and its date.

## Frontend

One new card type in the existing Angular app (`frontend/src/app/`), following the
`session-card` pattern: title, markdown body, option buttons, optional free-text field,
dismiss. Plus a quiet heartbeat line — `last checked 3h ago` — that turns visibly stale
past a threshold.

## Deliberately out of scope

* **Auth on the dashboard.** It binds localhost and holds low-sensitivity decisions.
* **Multi-project producers.** The store is generic; the producer is RET-specific. A
  second producer is additive when it is actually wanted.
* **Cleaning up the 13 stale worktrees.** Real, but its own job — likely the first
  hygiene wildcard this thing surfaces.

## Where the rules actually live

Worth knowing when changing this: the invariants are enforced in the **store**, not in the
prompt. A prompt can misbehave; a unique index cannot.

* **No duplicate questions** — unique index on `Fingerprint`, plus reopen-in-place
  semantics in `QuestionService.UpsertAsync`.
* **Cap of 3** — `QuestionService.OpenQuestionCap`, returned as a `409` to the producer.
  Reopens are allowed through at the cap on purpose: otherwise a full pool could
  permanently hide a subject that changed.
* **A pending answer is never clobbered** — an ask against an `answered` row returns
  `pending-answer` and writes nothing.
* **Write/Edit unavailable — but this is a reduction, not a guarantee.** The wrapper passes
  `--disallowedTools Write Edit NotebookEdit`, and that genuinely holds: verified
  2026-08-03 that a run asked to use `Write` under `bypassPermissions` returns `BLOCKED`
  and creates nothing. It removes the most likely accidental path — an agent deciding to
  "just fix" a file it noticed. It does **not** make repo modification impossible: `Bash`
  must stay allowed for `linctl` and `curl`, and shell redirection writes files perfectly
  well (also verified). So "this job preps, it never writes code or touches git" rests on
  the prompt constraint. Treat it as instruction-enforced, and if that ever needs to be
  mechanical, the answer is a narrower `Bash(...)` allowlist rather than more
  `--disallowedTools`.
* **No wasted token on a no-op run** — the pool check is in `run-decision-inbox.ps1`
  before `claude` is invoked at all.

## Verified behaviour

Exercised against the running container on 2026-08-03 (22 assertions, all passing),
including the two cases most likely to break in a redesign:

* An ask carrying the **post-write** hash is `suppressed` — the job's own section delete
  does not re-trigger the question it just answered.
* An ask carrying a **different** hash is `reopened`, the stale answer is cleared, and the
  prior answer is preserved in `History`.

## Known limitations

**No lock on the `answered → applied` transition.** Two concurrent runs could both read
the same answered question and both write to Linear, producing duplicate comments and
description appends. In practice one task with `MultipleInstances IgnoreNew` prevents this,
and that is the whole mitigation — so do not register a second task pointing at the same
runner. (Learned the hard way: registering an `S4UTEST` task alongside the live one on
2026-08-04 produced exactly that race. `IgnoreNew` is scoped to a single task name and did
not help. No duplicate writes resulted, but nothing in the store guaranteed that.)

**The window is a logon-type property, not a flag.** `-WindowStyle Hidden` still flashes a
console, and the task's `Hidden` setting only hides the task from the Task Scheduler list.
Only the logon type decides it: `Interactive` runs in the user's desktop session (verified:
`SessionId 1`) so a terminal pops to the foreground on every fire; `S4U` runs as the user
in the non-interactive session (`SessionId 0`) where no window can appear. S4U is the
default; `-ShowWindow` opts back in.

## Failure mode to watch

If this grows past *one cron, one table, one card list*, it is failing on the same
grounds it was built to serve. The value is decoupling decision latency from session
presence — not building a queue to manage the queue.
