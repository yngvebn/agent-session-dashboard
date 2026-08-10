<#
.SYNOPSIS
    Registers (or removes) the decision-inbox scheduled task.

.DESCRIPTION
    Creates a Windows scheduled task that runs run-decision-inbox.ps1 once a minute, as
    the current user.

    By default the job is MANUAL-ONLY (-CadenceMinutes 0): a tick does nothing unless the
    "Run now" button on the dashboard has left a request, which the tick claims. The
    every-minute tick is only a poller — it exists so a button press is serviced within a
    minute, since the container cannot start a host process itself. A tick with no request
    does one HTTP GET and exits, writing nothing.

    Pass a positive -CadenceMinutes to also run on the clock (e.g. -CadenceMinutes 30 for
    the old behaviour: act whenever the last run is 30+ minutes old).

    Running as the user is not optional: both `linctl` (~/.linctl-auth.json) and Claude
    (~/.claude) read per-profile credentials. Registered as SYSTEM the job would
    authenticate as nobody and fail on every run. Claude uses its default config root,
    so nothing about CLAUDE_CONFIG_DIR needs setting — the task inherits the user profile
    and finds ~/.claude on its own. (This was not always so: a second config root once
    forced explicit pinning. The roots are now consolidated into ~/.claude.)

    By default the task uses the S4U logon type, which runs as the user without an
    interactive session — so no console window appears. Pass -ShowWindow to run it
    inside your session instead, which pops a terminal to the foreground on every
    fire (occasionally handy for watching a run, annoying the rest of the time).

.PARAMETER CadenceMinutes
    0 (default) = manual-only. A positive value also runs on the clock, that many minutes
    after the last run.

.PARAMETER ShowWindow
    Use the Interactive logon type so a console window is visible while the job runs.
    Off by default.

.EXAMPLE
    pwsh scripts/register-decision-inbox-task.ps1                     # manual-only
    pwsh scripts/register-decision-inbox-task.ps1 -CadenceMinutes 30  # also every 30 min
    pwsh scripts/register-decision-inbox-task.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [string]$TaskName        = 'Decision Inbox',
    [string]$RepoPath        = 'C:\github\retrospective.fun',
    [string]$DashboardUrl    = 'http://localhost:5900',
    # How often the task fires at all. Ticking once a minute is what makes the dashboard's
    # "Run now" button responsive: a press is picked up on the next tick. Independent of
    # -CadenceMinutes, which decides whether a tick acts on the clock (0 = manual-only).
    [int]$IntervalMinutes    = 1,
    [int]$CadenceMinutes     = 0,
    [int]$TimeoutMinutes     = 20,
    [switch]$ShowWindow,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$runner = Join-Path $PSScriptRoot 'run-decision-inbox.ps1'
if (-not (Test-Path $runner)) { throw "Runner not found: $runner" }

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'." -ForegroundColor Yellow
    }
    else {
        Write-Host "No scheduled task named '$TaskName'." -ForegroundColor DarkYellow
    }
    return
}

$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { throw 'pwsh not found on PATH.' }

# Resolve `claude` the way the TASK will see it, not the way this shell does.
#
# Checking $env:PATH here gives the wrong answer in both directions. A shell opened
# before the CLI was installed has a stale PATH and reports a scary false alarm, while
# a shell with a session-local PATH addition reports success for something the
# scheduler will never find. Task Scheduler builds its environment from the persistent
# Machine + User PATH, so that is what to test.
$persistentPath = @(
    [Environment]::GetEnvironmentVariable('PATH', 'Machine')
    [Environment]::GetEnvironmentVariable('PATH', 'User')
) -join ';'

$claudeForTask = pwsh -NoProfile -Command "`$env:PATH = '$($persistentPath -replace "'", "''")'
    (Get-Command claude -ErrorAction SilentlyContinue).Source"
$claudeHere = (Get-Command claude -ErrorAction SilentlyContinue).Source

if (-not $claudeForTask) {
    if ($claudeHere) {
        # The dangerous direction: passes by hand, fails on schedule.
        Write-Warning "claude resolves in THIS shell ($claudeHere) but not from the persistent PATH."
        Write-Warning 'The scheduled task will not find it. Add its directory to the User PATH permanently.'
    }
    else {
        Write-Warning 'claude CLI not found on the persistent PATH — the task will fail until it is.'
    }
}
elseif (-not $claudeHere) {
    Write-Host "  Note    : claude not on this shell's PATH, but the task will find it at $claudeForTask." -ForegroundColor DarkGray
    Write-Host "            (Your shell predates the install; a new terminal will pick it up.)" -ForegroundColor DarkGray
}

# Verify the thing most likely to be missing on a fresh machine, since a job that
# cannot authenticate produces a heartbeat full of failures and nothing else.
$authFile = Join-Path $HOME '.linctl-auth.json'
if (-not (Test-Path $authFile)) {
    Write-Warning "No $authFile — run 'linctl auth' before relying on this task."
}

# Claude uses its default config root (~/.claude), which the task finds via the user
# profile — no CLAUDE_CONFIG_DIR to set or bake. Nothing to verify here beyond linctl
# above; if ~/.claude is ever unauthenticated the first run records a clear auth failure.

$arguments = @(
    '-NoProfile'
    '-NonInteractive'
    '-ExecutionPolicy', 'Bypass'
    '-File', "`"$runner`""
    '-RepoPath', "`"$RepoPath`""
    '-DashboardUrl', "`"$DashboardUrl`""
    '-CadenceMinutes', $CadenceMinutes
    '-TimeoutMinutes', $TimeoutMinutes
) -join ' '

$action = New-ScheduledTaskAction -Execute $pwsh -Argument $arguments -WorkingDirectory $PSScriptRoot

# Two triggers: catch up shortly after logon, then keep ticking. The logon delay
# gives Docker time to bring the dashboard container up first.
$atLogon = New-ScheduledTaskTrigger -AtLogOn
$atLogon.Delay = 'PT3M'

$repeating = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(7) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)

# Keyed to the RUN's own budget, not the trigger interval.
#
# It used to be min(30, interval - 5), on the reasoning that a hung run must not bleed
# into the next window. That premise died with the one-minute tick: overlapping fires
# are now the normal case — IgnoreNew drops ~29 of every 30 — and deriving from the
# interval would give a 5-minute limit that kills every real run, which take 8-13.
#
# So the only constraint left is that the scheduler's hard kill must come *after* the
# runner's own Wait-Job guard, which stops a run AND records a 'failed' heartbeat. A
# scheduler kill records nothing, so it should only ever be the backstop.
# Matches JobRunService.RunLease, which is how the store decides an unfinished run was
# abandoned; keep the two in step.
$executionLimit = $TimeoutMinutes + 5

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes $executionLimit)

# Logon type decides whether a console window appears, and it is the ONLY thing that
# reliably does. `-WindowStyle Hidden` still flashes a window, and the task's `Hidden`
# setting only hides the task from the Task Scheduler list — neither helps.
#
#   S4U         runs as the user in a non-interactive session: no window, ever. No
#               stored password. The user profile is still loaded, so ~/.linctl-auth.json
#               and ~/.claude resolve normally (verified).
#   Interactive runs inside the logged-on session, so a console window pops to the
#               foreground on every fire. Only useful for watching it work.
#
# Tradeoff of S4U: it also fires while the machine is on but nobody is logged in, when
# Docker Desktop is not running. The wrapper handles that quietly — it cannot reach the
# dashboard, so it cannot record a heartbeat either, and writes nothing at all rather
# than a run of noisy 'failed' rows. The heartbeat simply ages until you log back in.
$logonType = if ($ShowWindow) { 'Interactive' } else { 'S4U' }
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType $logonType -RunLevel Limited

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Replacing existing task '$TaskName'." -ForegroundColor DarkYellow
}

Register-ScheduledTask -TaskName $TaskName `
    -Action $action `
    -Trigger @($atLogon, $repeating) `
    -Settings $settings `
    -Principal $principal `
    -Description 'Decision inbox for retrospective.fun — asks the founder about stalled backlog decisions and applies the answers. See agent-session-dashboard/docs/DECISION-INBOX.md' | Out-Null

$cadenceDesc = if ($CadenceMinutes -gt 0) { "acts on request or every $CadenceMinutes min" } else { "acts on 'Run now' request only (manual-only)" }
Write-Host "Registered '$TaskName': ticks every $IntervalMinutes min, $cadenceDesc, plus a tick 3 min after logon." -ForegroundColor Green
Write-Host "  Runs as : $env:USERDOMAIN\$env:USERNAME ($logonType$(if (-not $ShowWindow) { ' — no console window' } else { ' — console window will appear' }))"
Write-Host "  Repo    : $RepoPath"
Write-Host "  Runner  : $runner"
Write-Host "  Config  : Claude default root (~/.claude)"
Write-Host "  Limit   : ${executionLimit} min per run (scheduler backstop)"
Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName'"
Write-Host "  curl.exe -s $DashboardUrl/api/jobs/runs?name=decision-inbox`&limit=5"
