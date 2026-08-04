<#
.SYNOPSIS
    Registers (or removes) the decision-inbox scheduled task.

.DESCRIPTION
    Creates a Windows scheduled task that runs run-decision-inbox.ps1 on logon and
    then every 30 minutes, as the current user.

    A short interval is close to free: the runner's pre-flight skips without invoking
    Claude whenever the pool is full and nothing is answered, which is most fires. The
    real throttle on cost is how fast questions get answered, not how often this ticks.

    Running as the user is not optional: `linctl` reads credentials from
    ~/.linctl-auth.json, which is per-profile. Registered as SYSTEM the job would
    authenticate as nobody and fail on every run.

    By default the task uses the S4U logon type, which runs as the user without an
    interactive session — so no console window appears. Pass -ShowWindow to run it
    inside your session instead, which pops a terminal to the foreground on every
    fire (occasionally handy for watching a run, annoying the rest of the time).

.PARAMETER ShowWindow
    Use the Interactive logon type so a console window is visible while the job runs.
    Off by default.

.EXAMPLE
    pwsh scripts/register-decision-inbox-task.ps1
    pwsh scripts/register-decision-inbox-task.ps1 -IntervalMinutes 120
    pwsh scripts/register-decision-inbox-task.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [string]$TaskName        = 'Decision Inbox',
    [string]$RepoPath        = 'C:\github\retrospective.fun',
    [string]$DashboardUrl    = 'http://localhost:5900',
    [int]$IntervalMinutes    = 30,
    [string]$ConfigDir       = $env:CLAUDE_CONFIG_DIR,
    [switch]$AllowDefaultConfig,
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

# CLAUDE_CONFIG_DIR is per-process in interactive shells and absent at User/Machine
# scope, so a scheduled task inherits nothing and silently falls back to ~/.claude —
# a different config root with its own credentials. Bake the current value into the
# task arguments rather than hoping the environment provides it.
#
# This fails rather than warns: a warning here produces a task that looks registered,
# runs on schedule, and fails on auth every time — the exact silent rot the heartbeat
# exists to catch, and better not to create in the first place.
if (-not $ConfigDir) {
    if (-not $AllowDefaultConfig) {
        # List candidates rather than picking one. The presence of .credentials.json
        # says nothing about whether those credentials still WORK — on this machine
        # both roots have the file and only one authenticates, so a confident guess
        # here would send you to the broken one.
        $candidates = Get-ChildItem $HOME -Directory -Filter '.claude*' -ErrorAction SilentlyContinue |
                      Where-Object { Test-Path (Join-Path $_.FullName '.credentials.json') }

        $hint = if ($candidates) {
            "`n`nConfig roots found (having credentials does NOT mean they are still valid —" +
            "`nif unsure, run 'claude auth status' with CLAUDE_CONFIG_DIR set to each):`n" +
            (($candidates | ForEach-Object { "  -ConfigDir '$($_.FullName)'" }) -join "`n")
        } else { '' }
        throw @"
CLAUDE_CONFIG_DIR is not set in this shell, so the task cannot be pinned to a config root.

That variable is per-process — it does not exist at User or Machine scope — so the
scheduled task would fall back to the default root. If that root is not authenticated,
EVERY run fails on auth while manual runs pass.

Pass it explicitly:
  pwsh scripts/register-decision-inbox-task.ps1 -ConfigDir '<path>'$hint

Or, if the default root really is the authenticated one:
  pwsh scripts/register-decision-inbox-task.ps1 -AllowDefaultConfig
"@
    }
    else {
        Write-Warning 'Registering against the default Claude config root (-AllowDefaultConfig).'
    }
}
elseif (-not (Test-Path $ConfigDir)) {
    throw "ConfigDir does not exist: $ConfigDir"
}

$arguments = @(
    '-NoProfile'
    '-NonInteractive'
    '-ExecutionPolicy', 'Bypass'
    '-File', "`"$runner`""
    '-RepoPath', "`"$RepoPath`""
    '-DashboardUrl', "`"$DashboardUrl`""
) + $(if ($ConfigDir) { @('-ConfigDir', "`"$ConfigDir`"") } else { @() }) -join ' '

$action = New-ScheduledTaskAction -Execute $pwsh -Argument $arguments -WorkingDirectory $PSScriptRoot

# Two triggers: catch up shortly after logon, then keep ticking. The logon delay
# gives Docker time to bring the dashboard container up first.
$atLogon = New-ScheduledTaskTrigger -AtLogOn
$atLogon.Delay = 'PT3M'

$repeating = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(7) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)

# Derived from the interval rather than fixed. The limit has to sit between two other
# numbers: above the runner's own 20-minute Wait-Job guard, which stops a run *and*
# records a 'failed' heartbeat, and below the next trigger, so a hung run cannot bleed
# into the following window. A hard-coded 30 satisfied neither at -IntervalMinutes 30 —
# it put the scheduler's hard kill exactly where the next fire wanted to start, which
# quietly demotes the backstop to a coin flip with IgnoreNew.
$executionLimit = [Math]::Min(30, [Math]::Max(5, $IntervalMinutes - 5))

if ($executionLimit -lt 20) {
    Write-Warning @"
Execution limit is ${executionLimit}m, below the runner's own 20m timeout. The scheduler
will hard-kill runs before run-decision-inbox.ps1 can record a 'failed' heartbeat, so a
slow run looks like a silently missing one. Either raise -IntervalMinutes, or lower
-TimeoutMinutes in the runner to stay under ${executionLimit}m.
"@
}

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
#               and CLAUDE_CONFIG_DIR resolve normally (verified).
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

Write-Host "Registered '$TaskName': every $IntervalMinutes min, plus 3 min after logon." -ForegroundColor Green
Write-Host "  Runs as : $env:USERDOMAIN\$env:USERNAME ($logonType$(if (-not $ShowWindow) { ' — no console window' } else { ' — console window will appear' }))"
Write-Host "  Repo    : $RepoPath"
Write-Host "  Runner  : $runner"
Write-Host "  Config  : $(if ($ConfigDir) { $ConfigDir } else { '<Claude default>' })"
Write-Host "  Limit   : ${executionLimit} min per run (scheduler backstop)"
Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName'"
Write-Host "  curl.exe -s $DashboardUrl/api/jobs/runs?name=decision-inbox`&limit=5"
