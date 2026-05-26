# Installs the session-reporter hook into ~/.claude/ and ~/.copilot/hooks/
# Run once: pwsh -File hooks/install.ps1

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceScript = Join-Path $scriptDir "session-reporter.ps1"

# ── Copy hook script ──────────────────────────────────────────────────────────

$hooksDir = Join-Path $env:USERPROFILE ".claude\hooks"
New-Item -ItemType Directory -Force $hooksDir | Out-Null
Copy-Item $sourceScript (Join-Path $hooksDir "session-reporter.ps1") -Force
$hookScript = Join-Path $hooksDir "session-reporter.ps1"

# ── Build command strings ─────────────────────────────────────────────────────

$heartbeatCmd    = "pwsh -NonInteractive -File `"$hookScript`" -Event heartbeat"
$closedCmd       = "pwsh -NonInteractive -File `"$hookScript`" -Event closed"
$startedCmd      = "pwsh -NonInteractive -File `"$hookScript`" -Event started"
$subagentCmd     = "pwsh -NonInteractive -File `"$hookScript`" -Event subagent-start"
$notificationCmd = "pwsh -NonInteractive -File `"$hookScript`" -Event notification"
$wtCreateCmd     = "pwsh -NonInteractive -File `"$hookScript`" -Event worktree-create"
$wtRemoveCmd     = "pwsh -NonInteractive -File `"$hookScript`" -Event worktree-remove"

# ── Claude Code (~/.claude/settings.json) ────────────────────────────────────

$claudeHookConfig = @{
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

$claudeSettingsPath = Join-Path $env:USERPROFILE ".claude\settings.json"
if (Test-Path $claudeSettingsPath) {
    $claudeSettings = Get-Content $claudeSettingsPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    $claudeSettings = @{}
}
$claudeSettings["hooks"] = $claudeHookConfig
$claudeSettings | ConvertTo-Json -Depth 10 | Set-Content $claudeSettingsPath -Encoding UTF8

Write-Host "[Claude Code] hooks installed → $claudeSettingsPath"

# ── GitHub Copilot (~/.copilot/hooks/session-dashboard.json) ─────────────────

$copilotHooksDir = Join-Path $env:USERPROFILE ".copilot\hooks"
New-Item -ItemType Directory -Force $copilotHooksDir | Out-Null

$copilotHookConfig = @{
    hooks = @{
        SessionStart = @(
            @{ type = "command"; command = $startedCmd; timeout = 5 }
        )
        Stop = @(
            @{ type = "command"; command = $closedCmd; timeout = 5 }
        )
        PostToolUse = @(
            @{ type = "command"; command = $heartbeatCmd; timeout = 5 }
        )
        UserPromptSubmit = @(
            @{ type = "command"; command = $heartbeatCmd; timeout = 5 }
        )
        SubagentStart = @(
            @{ type = "command"; command = $subagentCmd; timeout = 5 }
        )
        SubagentStop = @(
            @{ type = "command"; command = $heartbeatCmd; timeout = 5 }
        )
    }
}

$copilotConfigPath = Join-Path $copilotHooksDir "session-dashboard.json"
$copilotHookConfig | ConvertTo-Json -Depth 10 | Set-Content $copilotConfigPath -Encoding UTF8

Write-Host "[Copilot]      hooks installed → $copilotConfigPath"

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Hook script: $hookScript"
Write-Host "Restart Claude Code and VS Code for hooks to take effect."
