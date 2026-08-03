<#
.SYNOPSIS
    One run of the decision-inbox job. See docs/DECISION-INBOX.md.

.DESCRIPTION
    Drains answered questions (applying them to Linear) then tops the pool up with
    new ones. Both phases happen inside a single `claude -p` invocation.

    The cheap pre-flight lives HERE rather than in the prompt: a no-op run must not
    cost a session spin-up, which is what makes an hourly trigger essentially free.

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
    [int]$TimeoutMinutes  = 20,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$startedAt = (Get-Date).ToUniversalTime()
$promptPath = Join-Path $PSScriptRoot 'decision-inbox-prompt.md'

function Write-Log([string]$Message) {
    Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] $Message"
}

function Record-Run([string]$Outcome, [string]$Detail, [int]$Created = 0, [int]$Applied = 0) {
    if ($DryRun) {
        Write-Log "DRY RUN — would record: outcome=$Outcome created=$Created applied=$Applied detail=$Detail"
        return
    }
    try {
        $body = @{
            jobName   = $JobName
            outcome   = $Outcome
            detail    = $Detail
            startedAt = $startedAt.ToString('o')
            created   = $Created
            applied   = $Applied
        } | ConvertTo-Json -Compress

        Invoke-RestMethod -Method Post -Uri "$DashboardUrl/api/jobs/runs" `
            -ContentType 'application/json' -Body $body -TimeoutSec 15 | Out-Null
    }
    catch {
        # A lost heartbeat must never fail the run — the work may already have landed.
        Write-Warning "Could not record heartbeat: $($_.Exception.Message)"
    }
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

try {
    $questions = Invoke-RestMethod -Uri "$DashboardUrl/api/questions" -TimeoutSec 15
}
catch {
    # Dashboard down (container restarting, machine just booted). Not our problem
    # to fix, but it must be visible rather than silent.
    Record-Run -Outcome 'failed' -Detail "Dashboard unreachable at $DashboardUrl : $($_.Exception.Message)"
    throw
}

$open    = @($questions | Where-Object { $_.state -eq 'open' -or $_.state -eq 'failed' })
$pending = @($questions | Where-Object { $_.state -eq 'answered' })

Write-Log "open=$($open.Count) pending-apply=$($pending.Count) cap=$Cap"

# Skip only when there is genuinely nothing to do. Skipping on a full pool alone
# would strand answered questions forever.
if ($open.Count -ge $Cap -and $pending.Count -eq 0) {
    Write-Log 'Pool full and nothing answered — skipping without invoking Claude.'
    Record-Run -Outcome 'skipped' -Detail "pool full ($($open.Count)/$Cap), nothing to apply"
    exit 0
}

$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) {
    Record-Run -Outcome 'failed' -Detail 'claude CLI not found on PATH'
    throw 'claude CLI not found on PATH.'
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
        Record-Run -Outcome 'failed' -Detail "Timed out after $TimeoutMinutes minutes. Transcript: $transcript"
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
    Record-Run -Outcome 'failed' -Detail $detail
    Write-Warning $detail
    exit 1
}

if (-not $summary) {
    $tail = ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 3) -join ' / '
    Record-Run -Outcome 'failed' -Detail "No summary line in output. Tail: $tail. Transcript: $transcript"
    Write-Warning "Run produced no parseable summary. See $transcript"
    exit 1
}

$created = if ($summary -match 'created=(\d+)') { [int]$Matches[1] } else { 0 }
$applied = if ($summary -match 'applied=(\d+)') { [int]$Matches[1] } else { 0 }
$failed  = if ($summary -match 'failed=(\d+)')  { [int]$Matches[1] } else { 0 }

$outcome = if ($failed -gt 0) { 'failed' } else { 'ok' }
Record-Run -Outcome $outcome -Detail $summary.Trim() -Created $created -Applied $applied

Write-Log "Done: $summary"
exit 0
