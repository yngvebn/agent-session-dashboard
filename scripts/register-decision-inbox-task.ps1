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

$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) { Write-Warning 'claude CLI not on PATH — the task will fail until it is.' }

# Verify the thing most likely to be missing on a fresh machine, since a job that
# cannot authenticate produces a heartbeat full of failures and nothing else.
$authFile = Join-Path $HOME '.linctl-auth.json'
if (-not (Test-Path $authFile)) {
    Write-Warning "No $authFile — run 'linctl auth' before relying on this task."
}

$arguments = @(
    '-NoProfile'
    '-NonInteractive'
    '-ExecutionPolicy', 'Bypass'
    '-File', "`"$runner`""
    '-RepoPath', "`"$RepoPath`""
    '-DashboardUrl', "`"$DashboardUrl`""
) -join ' '

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
Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName'"
Write-Host "  curl.exe -s $DashboardUrl/api/jobs/runs?name=decision-inbox`&limit=5"
