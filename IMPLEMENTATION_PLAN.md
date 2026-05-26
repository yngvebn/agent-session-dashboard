# Agent Session Dashboard

A live-updating web dashboard that shows all active Claude Code sessions on your machine in real time.

## What It Does

Claude Code supports hooks — shell commands that fire on agent lifecycle events. This project wires those hooks to POST session metadata to a local backend, which stores it in SQLite and streams updates to a browser dashboard via Server-Sent Events (SSE).

Open the dashboard at `http://localhost:5900` and see every Claude session on your machine: what project it's working on, whether it's running or idle, and when it last did something.

---

## Architecture

```
Claude Code (any session)
  └── Global hooks (~/.claude/settings.json)
        └── PowerShell script (~/.claude/hooks/session-reporter.ps1)
              └── HTTP POST → localhost:5900/api/sessions/event

.NET 10 Backend (ASP.NET Core minimal API)
  ├── Receives hook POSTs
  ├── Persists to SQLite (rolling 7-day window)
  ├── Derives session status (running / idle / closed / crashed)
  └── Streams updates to browser via SSE

Angular 20 Frontend
  ├── Served as static files from .NET wwwroot (single container)
  ├── Connects to SSE endpoint on load
  └── Renders session cards, live-updated without polling
```

Single Docker container. One `docker-compose.yml`. SQLite file on a mounted volume.

---

## Session Lifecycle

| Status    | Meaning                                             |
|-----------|-----------------------------------------------------|
| `running` | Heartbeat received within the last 30 seconds       |
| `idle`    | No heartbeat for >30s, no Stop hook fired yet       |
| `closed`  | Stop hook fired cleanly                             |
| `crashed` | No Stop hook + process PID no longer exists         |

Sessions older than 7 days are purged automatically.

---

## Session Metadata

Each session carries:

```json
{
  "sessionId": "abc123",
  "name": "agent-session-dashboard",
  "status": "running",
  "startedAt": "2026-05-26T10:00:00Z",
  "lastSeen": "2026-05-26T10:05:00Z",
  "pid": 12345
}
```

- `sessionId` — unique per Claude Code instance (from `$env:CLAUDE_SESSION_ID`)
- `name` — basename of the working directory
- `status` — derived by backend from event history + heartbeat age
- `startedAt` — timestamp of first event for this session
- `lastSeen` — timestamp of most recent event
- `pid` — process ID of the Claude Code process

---

## Hook Events

Three hook types configured globally in `~/.claude/settings.json`:

| Hook              | Fires when                        | Action                         |
|-------------------|-----------------------------------|--------------------------------|
| `PostToolUse`     | After any tool completes          | Heartbeat POST (throttled 10s) |
| `Stop`            | Agent turn completes (session still open) | Heartbeat POST          |
| `SubagentStop`    | A subagent finishes               | Heartbeat POST                 |
| `SessionEnd`      | Session truly closing             | Status → `closed`              |

### Hook Script

Location: `~/.claude/hooks/session-reporter.ps1`

```powershell
param(
    [string]$Event = "heartbeat"
)

$sessionId = $env:CLAUDE_SESSION_ID
$workingDir = $env:CLAUDE_WORKING_DIR
$pid = $PID

if (-not $sessionId) { exit 0 }

# Throttle heartbeats: skip if last POST was <10s ago
$throttleFile = "$env:TEMP\claude-hook-$sessionId.txt"
if ($Event -eq "heartbeat" -and (Test-Path $throttleFile)) {
    $lastWrite = (Get-Item $throttleFile).LastWriteTime
    if ((Get-Date) - $lastWrite -lt [TimeSpan]::FromSeconds(10)) {
        exit 0
    }
}
New-Item -ItemType File -Force -Path $throttleFile | Out-Null

$body = @{
    sessionId  = $sessionId
    name       = Split-Path $workingDir -Leaf
    event      = $Event
    pid        = $pid
    workingDir = $workingDir
    timestamp  = (Get-Date -Format "o")
} | ConvertTo-Json -Compress

try {
    Invoke-RestMethod `
        -Uri "http://localhost:5900/api/sessions/event" `
        -Method POST `
        -ContentType "application/json" `
        -Body $body `
        -TimeoutSec 2 | Out-Null
} catch {
    # Dashboard not running — fail silently, never block Claude
}
```

### Hook Configuration

Add to `~/.claude/settings.json`:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NonInteractive -File ~/.claude/hooks/session-reporter.ps1 -Event heartbeat"
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NonInteractive -File ~/.claude/hooks/session-reporter.ps1 -Event closed"
          }
        ]
      }
    ],
    "SubagentStop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NonInteractive -File ~/.claude/hooks/session-reporter.ps1 -Event heartbeat"
          }
        ]
      }
    ]
  }
}
```

---

## Backend

**Runtime:** .NET 10, ASP.NET Core minimal API

**Endpoints:**

| Method | Path                     | Description                              |
|--------|--------------------------|------------------------------------------|
| POST   | `/api/sessions/event`    | Receives hook POSTs from Claude           |
| GET    | `/api/sessions`          | Returns all active sessions (JSON)        |
| GET    | `/api/sessions/stream`   | SSE stream of session change events       |

**SSE Event Format:**

```
event: session-update
data: {"sessionId":"abc123","name":"my-project","status":"running","lastSeen":"...","startedAt":"..."}

event: session-closed
data: {"sessionId":"abc123"}
```

**Status Derivation (background service, runs every 10s):**
1. Sessions with `lastSeen` > 30s ago and status `running` → mark `idle`
2. Sessions with `event=closed` → mark `closed`
3. Sessions idle > 5min → check if PID exists; if not → mark `crashed`
4. Purge sessions with `startedAt` older than 7 days

**Database:** SQLite via EF Core. Single `Sessions` table. File path configurable via env var `DB_PATH` (default: `/data/sessions.db`).

---

## Frontend

**Runtime:** Angular 20, standalone components

**Layout:**

```
┌─────────────────────────────────────────────┐
│  Agent Sessions              ● Live          │
├─────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐         │
│  │ my-project   │  │ other-repo   │         │
│  │ ● RUNNING    │  │ ○ IDLE       │         │
│  │ 12m ago      │  │ 3m ago       │         │
│  │ 45m running  │  │ 2h running   │         │
│  └──────────────┘  └──────────────┘         │
│  ┌──────────────┐                           │
│  │ old-project  │                           │
│  │ ✕ CLOSED     │  (dimmed)                 │
│  │ 1h ago       │                           │
│  └──────────────┘                           │
└─────────────────────────────────────────────┘
```

- Status badge colors: `running` = green, `idle` = yellow, `closed` = grey, `crashed` = red
- Active sessions (running/idle) sort before closed/crashed
- SSE connection indicator in top-right: green dot = connected, red = reconnecting
- Angular `EventSource` service reconnects automatically on disconnect
- No routing — single view

---

## Project Structure

```
agent-session-dashboard/
├── backend/
│   ├── AgentSessionDashboard.sln
│   └── AgentSessionDashboard/
│       ├── Program.cs
│       ├── Models/
│       │   └── Session.cs
│       ├── Data/
│       │   └── SessionDbContext.cs
│       ├── Services/
│       │   ├── SessionService.cs       # business logic + status derivation
│       │   └── SseService.cs           # manages SSE connections + broadcasts
│       ├── BackgroundServices/
│       │   └── SessionStatusWorker.cs  # runs every 10s, updates idle/crashed
│       └── wwwroot/                    # Angular build output goes here
├── frontend/
│   ├── angular.json
│   ├── src/
│   │   └── app/
│   │       ├── app.component.ts
│   │       ├── session-card/
│   │       │   └── session-card.component.ts
│   │       └── services/
│   │           └── session-sse.service.ts
│   └── package.json
├── docker-compose.yml
├── Dockerfile
└── README.md
```

---

## Docker

**Dockerfile** — multi-stage build:
1. Node stage: `npm ci && ng build` → outputs to `dist/`
2. .NET SDK stage: `dotnet publish` → copies Angular dist into `wwwroot/`
3. .NET runtime stage: final image, copies published output

**docker-compose.yml:**

```yaml
services:
  dashboard:
    build: .
    ports:
      - "5900:8080"
    volumes:
      - sessions-data:/data
    environment:
      - DB_PATH=/data/sessions.db
    restart: always

volumes:
  sessions-data:
```

Internal port `8080` (ASP.NET Core default in containers), mapped to `5900` on host.

**Run:**

```bash
docker compose up -d
```

**Rebuild after changes:**

```bash
docker compose up -d --build
```

---

## Environment Variables

| Variable  | Default           | Description                  |
|-----------|-------------------|------------------------------|
| `DB_PATH` | `/data/sessions.db` | SQLite database file path  |

---

## Local Development (without Docker)

```bash
# Terminal 1 — backend
cd backend/AgentSessionDashboard
dotnet run

# Terminal 2 — frontend (dev server with proxy to backend)
cd frontend
npm install
ng serve --proxy-config proxy.conf.json
```

Frontend dev server runs on `http://localhost:4200`, proxies `/api` and `/stream` to `http://localhost:5000`.

---

## Implementation Notes

- Backend must never return an error that blocks Claude hooks — the POST endpoint always returns `200 OK` even if the session write fails internally.
- The SSE endpoint must handle client disconnects gracefully — Angular's `EventSource` reconnects automatically.
- PID-based crash detection only works when the dashboard runs on the same machine as Claude Code (which is always true for localhost Docker).
- The throttle file in the hook script uses the session ID in the filename so concurrent sessions don't interfere.
