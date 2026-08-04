<#
.SYNOPSIS
    One run of the decision-inbox job. See docs/DECISION-INBOX.md.

.DESCRIPTION
    Drains answered questions (applying them to Linear) then tops the pool up with
    new ones. Both phases happen inside a single `claude -p` invocation.

    The cheap pre-flight lives HERE rather than in the prompt: a no-op run must not
    cost a session spin-up, which is what makes a short trigger interval essentially
    free — most fires skip on one HTTP GET.

    Ticks every minute; the CADENCE is separate from the trigger interval and lives
    here rather than in Task Scheduler. Two numbers that used to be one:

      trigger interval  how often this script runs at all (1 min)
      -CadenceMinutes   how often it does real work absent a manual request (30 min)

    Keeping the cadence in data — measured from the last recorded run — means it
    self-heals. After sleep or a missed window the next tick runs immediately instead
    of waiting for the next 30-minute boundary. It also lets the dashboard ask for a
    run it cannot start itself: the container has no way to launch a host process, so
    a button press becomes a JobRequests row that this script claims.

    A tick that decides "not yet" writes NOTHING. At one-minute granularity a
    heartbeat per tick would be ~1440 rows a day and would destroy the meaning of
    "last checked". A tick that decides to act but finds nothing to do still records
    `skipped`, exactly as before — so the heartbeat keeps its ~30-minute cadence and
    the UI's staleness threshold stays valid.

    Registered with Task Scheduler by register-decision-inbox-task.ps1. Must run as
    the interactive user — `linctl` credentials live in ~/.linctl-auth.json, which
    SYSTEM cannot see.

.PARAMETER RepoPath
    Working directory for the Claude session. Must be the retrospective.fun checkout
    so the job has code context when a question needs it.

.PARAMETER ConfigDir
    Value to force into CLAUDE_CONFIG_DIR. This is NOT cosmetic. The variable is set
    per-process in interactive shells and does not exist at User or Machine scope, so
    a task launched by the scheduler inherits nothing and falls back to ~/.claude —
    a different config root, with its own credentials and its own memory. On this
    machine that root's OAuth session is expired, so every scheduled run would have
    failed on auth while manual runs passed. Pinning it is the fix.

.PARAMETER DryRun
    Runs the pre-flight and prints what it would do, without invoking Claude and
    without recording a heartbeat.
#>
[CmdletBinding()]
param(
    [string]$DashboardUrl = 'http://localhost:5900',
    [string]$RepoPath     = 'C:\github\retrospective.fun',
    [string]$JobName      = 'decision-inbox',
    [string]$ConfigDir    = $env:CLAUDE_CONFIG_DIR,
    [int]$Cap             = 3,
    [int]$CadenceMinutes  = 30,
    [int]$TimeoutMinutes  = 20,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$startedAt = (Get-Date).ToUniversalTime()
$promptPath = Join-Path $PSScriptRoot 'decision-inbox-prompt.md'

function Write-Log([string]$Message) {
    Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $Message"
}

# Returns $null instead of throwing. At one-minute granularity an unreachable
# dashboard (container restarting, machine booted, nobody logged in) must be a quiet
# no-op: there is nowhere to record a failure, and a task that "fails" 1440 times a
# day buries the real ones in Task Scheduler history.
function Get-Json([string]$Path) {
    try { return Invoke-RestMethod -Uri "$DashboardUrl$Path" -TimeoutSec 15 }
    catch { return $null }
}

function Post-Json([string]$Path, $Body) {
    try {
        return Invoke-RestMethod -Method Post -Uri "$DashboardUrl$Path" `
            -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 15
    }
    catch {
        Write-Warning "POST $Path failed: $($_.Exception.Message)"
        return $null
    }
}

# One-shot record, for a tick that acted but found nothing to do.
function Record-Run([string]$Outcome, [string]$Detail, [int]$Created = 0, [int]$Applied = 0) {
    if ($DryRun) {
        Write-Log "DRY RUN — would record: outcome=$Outcome created=$Created applied=$Applied detail=$Detail"
        return
    }
    # A lost heartbeat must never fail the run — the work may already have landed.
    Post-Json '/api/jobs/runs' @{
        jobName   = $JobName
        outcome   = $Outcome
        detail    = $Detail
        startedAt = $startedAt.ToString('o')
        created   = $Created
        applied   = $Applied
    } | Out-Null
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
# Anything that can be decided without an LLM is decided here.

if (-not (Test-Path $promptPath)) {
    Record-Run -Outcome 'failed' -Detail "Prompt file missing: $promptPath"
    throw "Prompt file not found at $promptPath"
}

if (-not (Test-Path $RepoPath)) {
    Record-Run -Outcome 'failed' -Detail "Repo path missing: $RepoPath"
    throw "RepoPath not found: $RepoPath. The job needs it for code context."
}

# ── Should this tick act at all? ──────────────────────────────────────────────
# Answered from the heartbeat, before anything else is fetched. Silent on every
# "no" — see the note in .DESCRIPTION about why a per-tick heartbeat is harmful.

$lastRun = @(Get-Json "/api/jobs/runs?name=$JobName&limit=1")
if ($null -eq $lastRun) {
    Write-Log "Dashboard unreachable at $DashboardUrl — nothing to do, and nowhere to say so."
    exit 0
}
$lastRun = $lastRun | Select-Object -First 1

# A run already in flight. Belt and braces with the task's IgnoreNew, and gated on the
# LEASE rather than on the bare absence of finishedAt: a hard-killed run leaves an
# unfinished row forever, and treating that as "still running" would wedge the job
# permanently while looking merely quiet.
if ($lastRun -and -not $lastRun.finishedAt) {
    $inFlightFor = (Get-Date).ToUniversalTime() - ([datetime]$lastRun.startedAt).ToUniversalTime()
    if ($inFlightFor.TotalMinutes -lt ($TimeoutMinutes + 5)) {
        Write-Log ("A run has been in flight for {0:N0}m — leaving it alone." -f $inFlightFor.TotalMinutes)
        exit 0
    }
    Write-Log ("Abandoning a stale in-flight run ({0:N0}m old, past its lease)." -f $inFlightFor.TotalMinutes)
}

$requests = @(Get-Json "/api/jobs/requests?name=$JobName")
$requested = $requests.Count -gt 0

$sinceLast = if ($lastRun) {
    (Get-Date).ToUniversalTime() - ([datetime]$lastRun.startedAt).ToUniversalTime()
} else {
    [TimeSpan]::MaxValue    # never run: go now
}
$dueByCadence = $sinceLast.TotalMinutes -ge $CadenceMinutes

if (-not $requested -and -not $dueByCadence) {
    # The common case, ~29 ticks out of 30. Deliberately writes nothing.
    Write-Verbose ("Not due yet ({0:N0}m of {1}m) and nothing requested." -f $sinceLast.TotalMinutes, $CadenceMinutes)
    exit 0
}

$trigger = if ($requested) { "requested manually ($($requests.Count) press(es))" } else { "cadence ($CadenceMinutes m)" }
Write-Log "Acting: $trigger"

$questions = Get-Json '/api/questions'
if ($null -eq $questions) {
    Write-Log 'Dashboard went away between calls — leaving it for the next tick.'
    exit 0
}

$open    = @($questions | Where-Object { $_.state -eq 'open' -or $_.state -eq 'failed' })
$pending = @($questions | Where-Object { $_.state -eq 'answered' })

Write-Log "open=$($open.Count) pending-apply=$($pending.Count) cap=$Cap"

# Claim before deciding to skip, not after. An unclaimed request would be re-evaluated
# every minute forever; you asked, it looked, and "nothing to do" is a real answer.
if ($requested -and -not $DryRun) {
    Post-Json '/api/jobs/requests/claim' @{ jobName = $JobName } | Out-Null
}

# Skip only when there is genuinely nothing to do. Skipping on a full pool alone
# would strand answered questions forever.
if ($open.Count -ge $Cap -and $pending.Count -eq 0) {
    Write-Log 'Pool full and nothing answered — skipping without invoking Claude.'
    Record-Run -Outcome 'skipped' -Detail "pool full ($($open.Count)/$Cap), nothing to apply; trigger: $trigger"
    exit 0
}

$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) {
    # A shell opened before the CLI was installed has a stale PATH — PATH changes never
    # propagate into running processes. Fall back to the persistent value so a manual run
    # from an old terminal still works. (Under the scheduler this rarely triggers: the
    # task's own environment is built from the persistent PATH to begin with.)
    $persistentPath = @(
        [Environment]::GetEnvironmentVariable('PATH', 'Machine')
        [Environment]::GetEnvironmentVariable('PATH', 'User')
    ) -join ';'

    foreach ($dir in ($persistentPath -split ';' | Where-Object { $_ })) {
        $candidate = Join-Path $dir 'claude.exe'
        if (Test-Path $candidate) { $claude = $candidate; break }
    }

    if ($claude) {
        Write-Log "claude not on this shell's PATH; using $claude from the persistent PATH."
    }
    else {
        Record-Run -Outcome 'failed' -Detail 'claude CLI not found on PATH (checked process and persistent PATH)'
        throw 'claude CLI not found on PATH.'
    }
}

if ($ConfigDir) {
    if (-not (Test-Path $ConfigDir)) {
        Record-Run -Outcome 'failed' -Detail "ConfigDir does not exist: $ConfigDir"
        throw "ConfigDir not found: $ConfigDir"
    }
    $env:CLAUDE_CONFIG_DIR = $ConfigDir
    Write-Log "CLAUDE_CONFIG_DIR = $ConfigDir"
}
else {
    # Better to say so than to silently use whichever root happens to be default —
    # they can differ in credentials, memory and permissions.
    Write-Warning 'No ConfigDir given; using the default Claude config root. Pass -ConfigDir if this machine uses a non-default one.'
}

if ($DryRun) {
    Write-Log "DRY RUN — would invoke: $claude -p (prompt from $promptPath) in $RepoPath"
    exit 0
}

# ── Run ───────────────────────────────────────────────────────────────────────

$prompt = Get-Content $promptPath -Raw
$transcript = Join-Path $env:TEMP "decision-inbox-$($startedAt.ToString('yyyyMMdd-HHmmss')).log"

# Open the in-flight row before doing any work: it is what stops the next tick from
# starting a second run, and what the dashboard reads to show "triage running".
$runRow = Post-Json '/api/jobs/runs/start' @{ jobName = $JobName }
$runId = $runRow.id
if ($runId) { Write-Log "Recorded in-flight run $runId" }
else { Write-Warning 'Could not open an in-flight run row; the UI will not show this run as running.' }

# Closes $runId if it was opened, else falls back to a one-shot record, so an outcome
# is never lost just because the start call failed.
function Complete-Run([string]$Outcome, [string]$Detail, [int]$Created = 0, [int]$Applied = 0) {
    if ($DryRun) { Write-Log "DRY RUN — would finish: $Outcome $Detail"; return }
    if ($runId) {
        Post-Json "/api/jobs/runs/$runId/finish" @{
            jobName = $JobName; outcome = $Outcome; detail = $Detail
            created = $Created; applied = $Applied
        } | Out-Null
    }
    else {
        Record-Run -Outcome $Outcome -Detail $Detail -Created $Created -Applied $Applied
    }
}

Push-Location $RepoPath
try {
    Write-Log "Invoking Claude in $RepoPath (transcript: $transcript)"

    # bypassPermissions is required for an unattended run — there is no human to
    # approve the linctl and curl calls. The tool surface is narrowed instead:
    # no Write/Edit, so the job cannot modify the repo even by accident, which is
    # also constraint 1 in the prompt enforced mechanically rather than by trust.
    $job = Start-Job -ScriptBlock {
        param($claude, $prompt, $repoPath, $transcript, $configDir)
        Set-Location $repoPath
        # Start-Job spawns a fresh process — the parent's $env: assignment above does
        # not carry over, so set it again here.
        if ($configDir) { $env:CLAUDE_CONFIG_DIR = $configDir }
        $prompt | & $claude -p `
            --permission-mode bypassPermissions `
            --allowedTools 'Bash' 'Read' 'Grep' 'Glob' `
            --disallowedTools 'Write' 'Edit' 'NotebookEdit' `
            --output-format text 2>&1 | Tee-Object -FilePath $transcript
    } -ArgumentList $claude, $prompt, $RepoPath, $transcript, $ConfigDir

    if (-not (Wait-Job $job -Timeout ($TimeoutMinutes * 60))) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        Complete-Run -Outcome 'failed' -Detail "Timed out after $TimeoutMinutes minutes. Transcript: $transcript"
        throw "Decision inbox run timed out after $TimeoutMinutes minutes."
    }

    $output = (Receive-Job $job) -join "`n"
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}

# ── Parse the summary line ────────────────────────────────────────────────────
# The prompt asks for `applied=<n> created=<n> reopened=<n> failed=<n> notes=<text>`.
# Missing or malformed output is reported rather than guessed at.

$summary = ($output -split "`n" | Where-Object { $_ -match 'applied=\d+' } | Select-Object -Last 1)

# Auth is the failure most likely to appear out of nowhere (token expiry, wrong config
# root) and the least obvious from a generic "no summary" message. Name it.
if ($output -match 'Failed to authenticate|OAuth session expired|Invalid API key') {
    $detail = "Claude auth failed (CLAUDE_CONFIG_DIR=$($ConfigDir ?? '<default>')). Run 'claude' interactively to re-authenticate that config root."
    Complete-Run -Outcome 'failed' -Detail $detail
    Write-Warning $detail
    exit 1
}

if (-not $summary) {
    $tail = ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 3) -join ' / '
    Complete-Run -Outcome 'failed' -Detail "No summary line in output. Tail: $tail. Transcript: $transcript"
    Write-Warning "Run produced no parseable summary. See $transcript"
    exit 1
}

$created = if ($summary -match 'created=(\d+)') { [int]$Matches[1] } else { 0 }
$applied = if ($summary -match 'applied=(\d+)') { [int]$Matches[1] } else { 0 }
$failed  = if ($summary -match 'failed=(\d+)')  { [int]$Matches[1] } else { 0 }

$outcome = if ($failed -gt 0) { 'failed' } else { 'ok' }
Complete-Run -Outcome $outcome -Detail $summary.Trim() -Created $created -Applied $applied

Write-Log "Done: $summary"
exit 0
