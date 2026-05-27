# Installs session-reporter hooks for Claude Code and GitHub Copilot.
# Cross-platform: Windows (pwsh + session-reporter.ps1), Linux/macOS (bash + session-reporter.sh)
# Run once: pwsh -File hooks/install.ps1

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$homeDir    = if ($env:HOME) { $env:HOME } else { $env:USERPROFILE }
$hooksDir   = Join-Path $homeDir ".claude/hooks"

New-Item -ItemType Directory -Force $hooksDir | Out-Null

# ── Copy hook scripts ─────────────────────────────────────────────────────────

Copy-Item (Join-Path $scriptDir "session-reporter.ps1") (Join-Path $hooksDir "session-reporter.ps1") -Force

$shScript = Join-Path $scriptDir "session-reporter.sh"
if (Test-Path $shScript) {
    Copy-Item $shScript (Join-Path $hooksDir "session-reporter.sh") -Force
    if ($IsLinux -or $IsMacOS) {
        chmod +x (Join-Path $hooksDir "session-reporter.sh")
    }
}

# ── Build command strings (OS-appropriate) ────────────────────────────────────

$psScript = Join-Path $hooksDir "session-reporter.ps1"
$bashScript = Join-Path $hooksDir "session-reporter.sh"

if ($IsWindows -or (-not $IsLinux -and -not $IsMacOS)) {
    $heartbeatCmd    = "pwsh -NonInteractive -File `"$psScript`" -Event heartbeat"
    $stopCmd         = "pwsh -NonInteractive -File `"$psScript`" -Event stop"
    $closedCmd       = "pwsh -NonInteractive -File `"$psScript`" -Event closed"
    $startedCmd      = "pwsh -NonInteractive -File `"$psScript`" -Event started"
    $subagentCmd     = "pwsh -NonInteractive -File `"$psScript`" -Event subagent-start"
    $notificationCmd = "pwsh -NonInteractive -File `"$psScript`" -Event notification"
    $wtCreateCmd     = "pwsh -NonInteractive -File `"$psScript`" -Event worktree-create"
    $wtRemoveCmd     = "pwsh -NonInteractive -File `"$psScript`" -Event worktree-remove"
} else {
    $heartbeatCmd    = "bash `"$bashScript`" heartbeat"
    $stopCmd         = "bash `"$bashScript`" stop"
    $closedCmd       = "bash `"$bashScript`" closed"
    $startedCmd      = "bash `"$bashScript`" started"
    $subagentCmd     = "bash `"$bashScript`" subagent-start"
    $notificationCmd = "bash `"$bashScript`" notification"
    $wtCreateCmd     = "bash `"$bashScript`" worktree-create"
    $wtRemoveCmd     = "bash `"$bashScript`" worktree-remove"
}

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
        @{ hooks = @(@{ type = "command"; command = $stopCmd }) }
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

$claudeSettingsPath = Join-Path $homeDir ".claude/settings.json"
if (Test-Path $claudeSettingsPath) {
    $claudeSettings = Get-Content $claudeSettingsPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    $claudeSettings = @{}
}
$claudeSettings["hooks"] = $claudeHookConfig
$claudeSettings | ConvertTo-Json -Depth 10 | Set-Content $claudeSettingsPath -Encoding UTF8

Write-Host "[Claude Code] hooks installed → $claudeSettingsPath"

# ── GitHub Copilot (~/.copilot/hooks/session-dashboard.json) ─────────────────

$copilotHooksDir = Join-Path $homeDir ".copilot/hooks"
New-Item -ItemType Directory -Force $copilotHooksDir | Out-Null

# Copilot supports per-OS command overrides in the same config file
$psHeartbeat    = "pwsh -NonInteractive -File `"$psScript`" -Event heartbeat"
$psClosed       = "pwsh -NonInteractive -File `"$psScript`" -Event closed"
$psStarted      = "pwsh -NonInteractive -File `"$psScript`" -Event started"
$psSubagent     = "pwsh -NonInteractive -File `"$psScript`" -Event subagent-start"
$bashHeartbeat  = "bash `"$bashScript`" heartbeat"
$bashClosed     = "bash `"$bashScript`" closed"
$bashStarted    = "bash `"$bashScript`" started"
$bashSubagent   = "bash `"$bashScript`" subagent-start"

$copilotHookConfig = @{
    hooks = @{
        SessionStart = @(@{
            type    = "command"
            command = $bashStarted
            windows = $psStarted
            timeout = 5
        })
        Stop = @(@{
            type    = "command"
            command = $bashClosed
            windows = $psClosed
            timeout = 5
        })
        PostToolUse = @(@{
            type    = "command"
            command = $bashHeartbeat
            windows = $psHeartbeat
            timeout = 5
        })
        UserPromptSubmit = @(@{
            type    = "command"
            command = $bashHeartbeat
            windows = $psHeartbeat
            timeout = 5
        })
        SubagentStart = @(@{
            type    = "command"
            command = $bashSubagent
            windows = $psSubagent
            timeout = 5
        })
        SubagentStop = @(@{
            type    = "command"
            command = $bashHeartbeat
            windows = $psHeartbeat
            timeout = 5
        })
    }
}

$copilotConfigPath = Join-Path $copilotHooksDir "session-dashboard.json"
$copilotHookConfig | ConvertTo-Json -Depth 10 | Set-Content $copilotConfigPath -Encoding UTF8

Write-Host "[Copilot]      hooks installed → $copilotConfigPath"

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Hook script (ps):   $psScript"
Write-Host "Hook script (bash): $bashScript"
Write-Host "Restart Claude Code and VS Code for hooks to take effect."
