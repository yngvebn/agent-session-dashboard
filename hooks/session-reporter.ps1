param(
    [string]$Event = "heartbeat"
)

# Read stdin JSON (Claude Code passes hook data via stdin)
$inputJson = $null
try {
    $inputJson = $input | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$sessionId = $inputJson.session_id
$cwd = $inputJson.cwd

if (-not $sessionId) { exit 0 }

# Throttle heartbeats: skip if last POST was <10s ago
$isHeartbeat = $Event -in @("heartbeat", "subagent-start")
$throttleFile = "$env:TEMP\claude-hook-$sessionId.txt"
if ($isHeartbeat -and (Test-Path $throttleFile)) {
    $lastWrite = (Get-Item $throttleFile).LastWriteTime
    if ((Get-Date) - $lastWrite -lt [TimeSpan]::FromSeconds(10)) {
        exit 0
    }
}
if ($isHeartbeat) {
    New-Item -ItemType File -Force -Path $throttleFile | Out-Null
}

$branch = $null
if ($cwd) {
    try {
        $branch = & git -C $cwd rev-parse --abbrev-ref HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { $branch = $null }
        else { $branch = $branch.Trim() }
        if ($branch -eq "HEAD") { $branch = $null }
    } catch { $branch = $null }
}

# Extract session title from transcript: custom-title > ai-title > cwd basename
$sessionName = if ($cwd) { Split-Path $cwd -Leaf } else { "unknown" }
$transcriptPath = $inputJson.transcript_path
if ($transcriptPath -and (Test-Path $transcriptPath)) {
    try {
        $customTitle = $null
        $aiTitle = $null
        Get-Content $transcriptPath | ForEach-Object {
            try {
                $entry = $_ | ConvertFrom-Json -ErrorAction Stop
                if ($entry.type -eq "custom-title" -and $entry.customTitle) { $customTitle = $entry.customTitle }
                if ($entry.type -eq "ai-title" -and $entry.aiTitle) { $aiTitle = $entry.aiTitle }
            } catch {}
        }
        if ($customTitle) { $sessionName = $customTitle }
        elseif ($aiTitle) { $sessionName = $aiTitle }
    } catch {}
}

$body = @{
    sessionId  = $sessionId
    name       = $sessionName
    event      = $Event
    pid        = $PID
    workingDir = if ($cwd) { $cwd } else { "" }
    timestamp  = (Get-Date -Format "o")
}
if ($branch) { $body["branch"] = $branch }

# Event-specific extra fields
if ($Event -eq "notification" -and $inputJson.message) {
    $body["message"] = $inputJson.message
}
if ($Event -eq "worktree-create" -and $inputJson.worktree_path) {
    $body["worktreePath"] = $inputJson.worktree_path
}

try {
    Invoke-RestMethod `
        -Uri "http://localhost:5900/api/sessions/event" `
        -Method POST `
        -ContentType "application/json" `
        -Body ($body | ConvertTo-Json -Compress) `
        -TimeoutSec 1 | Out-Null
} catch {
    # Dashboard not running — fail silently, never block Claude
}
