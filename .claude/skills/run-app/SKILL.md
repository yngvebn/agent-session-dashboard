---
name: run-app
description: Restart the agent-session-dashboard Docker Compose stack. Rebuilds the image (frontend + backend), recreates the container, and verifies the app is up at http://localhost:5900. Use whenever code changes need to be deployed locally.
triggers:
  - /run
  - restart the app
  - rebuild and restart
  - deploy locally
---

# Run / Restart the App

The app runs as a single Docker Compose service (`dashboard`) on port **5900**.
A full image rebuild is required on every code change (Dockerfile builds Angular then .NET).

## Steps

1. From the repo root (`C:\github\agent-session-dashboard`), run:

```powershell
docker compose down --remove-orphans
$env:GIT_SHA = (git rev-parse HEAD)
docker compose build --no-cache
docker compose up -d
```

2. Wait for the container to be healthy, then smoke-test:

```powershell
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Seconds 3
    try {
        $r = Invoke-WebRequest -Uri 'http://localhost:5900' -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { Write-Host "UP: HTTP $($r.StatusCode)"; break }
    } catch {}
    if ((Get-Date) -gt $deadline) { Write-Error "Timed out waiting for app"; break }
} while ($true)
```

3. Report the result to the user: URL, container status, any errors from `docker compose logs dashboard --tail 30`.

## Notes

- `--no-cache` ensures Angular and .NET artifacts are always fresh.
- The SQLite DB is on a named volume (`sessions-data`) and survives rebuilds.
- Port mapping: host **5900** → container **8080**.
- If `docker compose build` fails, show the last 50 lines of output — the most common cause is a frontend type error or a .NET publish failure.
