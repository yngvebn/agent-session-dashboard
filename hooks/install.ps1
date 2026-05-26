# Installs the session-reporter hook into both Claude account configs.
# Run once: pwsh -File hooks/install.ps1

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceScript = Join-Path $scriptDir "session-reporter.ps1"

# Hook script lives in ~/.claude/hooks/ — shared across accounts
$sharedHooksDir = Join-Path $env:USERPROFILE ".claude\hooks"
New-Item -ItemType Directory -Force $sharedHooksDir | Out-Null
Copy-Item $sourceScript (Join-Path $sharedHooksDir "session-reporter.ps1") -Force
$hookScript = Join-Path $sharedHooksDir "session-reporter.ps1"

$heartbeatCmd = "pwsh -NonInteractive -File `"$hookScript`" -Event heartbeat"
$closedCmd    = "pwsh -NonInteractive -File `"$hookScript`" -Event closed"

$startedCmd      = "pwsh -NonInteractive -File `"$hookScript`" -Event started"
$subagentCmd     = "pwsh -NonInteractive -File `"$hookScript`" -Event subagent-start"
$notificationCmd = "pwsh -NonInteractive -File `"$hookScript`" -Event notification"
$wtCreateCmd     = "pwsh -NonInteractive -File `"$hookScript`" -Event worktree-create"
$wtRemoveCmd     = "pwsh -NonInteractive -File `"$hookScript`" -Event worktree-remove"

$hookConfig = @{
    SessionStart = @(
        @{ hooks = @(@{ type = "command"; command = $startedCmd }) }
    )
    SessionEnd = @(
        @{ hooks = @(@{ type = "command"; command = $closedCmd }) }
    )
    PostToolUse = @(
        @{ matcher = ""; hooks = @(@{ type = "command"; command = $heartbeatCmd }) }
    )
    Stop = @(
        @{ hooks = @(@{ type = "command"; command = $heartbeatCmd }) }
    )
    SubagentStart = @(
        @{ hooks = @(@{ type = "command"; command = $subagentCmd }) }
    )
    SubagentStop = @(
        @{ hooks = @(@{ type = "command"; command = $heartbeatCmd }) }
    )
    Notification = @(
        @{ hooks = @(@{ type = "command"; command = $notificationCmd }) }
    )
    WorktreeCreate = @(
        @{ hooks = @(@{ type = "command"; command = $wtCreateCmd }) }
    )
    WorktreeRemove = @(
        @{ hooks = @(@{ type = "command"; command = $wtRemoveCmd }) }
    )
}

# Install into each account's config dir
$accounts = @("aurum", "gmail")
foreach ($account in $accounts) {
    $configDir = Join-Path $env:USERPROFILE ".claude-$account"
    if (-not (Test-Path $configDir)) {
        Write-Host "Skipping $account — config dir not found: $configDir"
        continue
    }

    $settingsPath = Join-Path $configDir "settings.json"

    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    } else {
        $settings = @{}
    }

    $settings["hooks"] = $hookConfig
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8

    Write-Host "[$account] hooks installed → $settingsPath"
}

Write-Host ""
Write-Host "Hook script: $hookScript"
Write-Host "Restart Claude Code for hooks to take effect."
