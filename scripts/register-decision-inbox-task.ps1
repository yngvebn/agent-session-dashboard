<#
.SYNOPSIS
    Registers (or removes) the decision-inbox scheduled task.

.DESCRIPTION
    Creates a Windows scheduled task that runs run-decision-inbox.ps1 on logon and
    then hourly, as the current interactive user.

    Running as the user is not optional: `linctl` reads credentials from
    ~/.linctl-auth.json, which is per-profile. Registered as SYSTEM the job would
    authenticate as nobody and fail on every run.

    "Run only when user is logged on" is deliberate — it encodes "local thing, only
    while my computer is on" rather than needing a stored password.

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
    [int]$IntervalMinutes    = 60,
    [string]$ConfigDir       = $env:CLAUDE_CONFIG_DIR,
    [switch]$AllowDefaultConfig,
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

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

# Interactive-only: no stored password, and the task simply does not run when the
# machine is off or nobody is logged in. That is the intended behaviour.
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

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
Write-Host "  Runs as : $env:USERDOMAIN\$env:USERNAME (interactive only)"
Write-Host "  Repo    : $RepoPath"
Write-Host "  Runner  : $runner"
Write-Host "  Config  : $(if ($ConfigDir) { $ConfigDir } else { '<Claude default>' })"
Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName'"
Write-Host "  curl.exe -s $DashboardUrl/api/jobs/runs?name=decision-inbox`&limit=5"
