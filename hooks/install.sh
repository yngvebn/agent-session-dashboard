#!/usr/bin/env bash
# Installs session-reporter hooks for Claude Code and GitHub Copilot.
# Requires: jq (for JSON manipulation)
# Run once: bash hooks/install.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Prereq check ──────────────────────────────────────────────────────────────

if ! command -v jq &>/dev/null; then
    echo "Error: jq is required. Install it first:"
    echo "  macOS:  brew install jq"
    echo "  Ubuntu: sudo apt install jq"
    exit 1
fi

# ── Copy hook script ──────────────────────────────────────────────────────────

HOOKS_DIR="$HOME/.claude/hooks"
mkdir -p "$HOOKS_DIR"
cp "$SCRIPT_DIR/session-reporter.sh" "$HOOKS_DIR/session-reporter.sh"
chmod +x "$HOOKS_DIR/session-reporter.sh"
BASH_SCRIPT="$HOOKS_DIR/session-reporter.sh"

# ── Build command strings ─────────────────────────────────────────────────────

HEARTBEAT_CMD="bash \"$BASH_SCRIPT\" heartbeat"
CLOSED_CMD="bash \"$BASH_SCRIPT\" closed"
STARTED_CMD="bash \"$BASH_SCRIPT\" started"
SUBAGENT_CMD="bash \"$BASH_SCRIPT\" subagent-start"
NOTIFICATION_CMD="bash \"$BASH_SCRIPT\" notification"
WT_CREATE_CMD="bash \"$BASH_SCRIPT\" worktree-create"
WT_REMOVE_CMD="bash \"$BASH_SCRIPT\" worktree-remove"

# ── Claude Code (~/.claude/settings.json) ────────────────────────────────────

CLAUDE_SETTINGS="$HOME/.claude/settings.json"
[ -f "$CLAUDE_SETTINGS" ] || echo '{}' > "$CLAUDE_SETTINGS"

HOOK_CONFIG=$(jq -n \
    --arg heartbeat  "$HEARTBEAT_CMD" \
    --arg closed     "$CLOSED_CMD" \
    --arg started    "$STARTED_CMD" \
    --arg subagent   "$SUBAGENT_CMD" \
    --arg notify     "$NOTIFICATION_CMD" \
    --arg wtCreate   "$WT_CREATE_CMD" \
    --arg wtRemove   "$WT_REMOVE_CMD" \
'{
    SessionStart:   [{ hooks: [{ type: "command", command: $started }] }],
    SessionEnd:     [{ hooks: [{ type: "command", command: $closed }] }],
    PostToolUse:    [{ matcher: "", hooks: [{ type: "command", command: $heartbeat }] }],
    Stop:           [{ hooks: [{ type: "command", command: $heartbeat }] }],
    SubagentStart:  [{ hooks: [{ type: "command", command: $subagent }] }],
    SubagentStop:   [{ hooks: [{ type: "command", command: $heartbeat }] }],
    Notification:   [{ hooks: [{ type: "command", command: $notify }] }],
    WorktreeCreate: [{ hooks: [{ type: "command", command: $wtCreate }] }],
    WorktreeRemove: [{ hooks: [{ type: "command", command: $wtRemove }] }]
}')

jq --argjson hooks "$HOOK_CONFIG" '.hooks = $hooks' "$CLAUDE_SETTINGS" > "$CLAUDE_SETTINGS.tmp"
mv "$CLAUDE_SETTINGS.tmp" "$CLAUDE_SETTINGS"

echo "[Claude Code] hooks installed → $CLAUDE_SETTINGS"

# ── GitHub Copilot (~/.copilot/hooks/session-dashboard.json) ─────────────────

COPILOT_HOOKS_DIR="$HOME/.copilot/hooks"
mkdir -p "$COPILOT_HOOKS_DIR"

jq -n \
    --arg heartbeat "$HEARTBEAT_CMD" \
    --arg closed    "$CLOSED_CMD" \
    --arg started   "$STARTED_CMD" \
    --arg subagent  "$SUBAGENT_CMD" \
'{
    hooks: {
        SessionStart:    [{ type: "command", command: $started,   timeout: 5 }],
        Stop:            [{ type: "command", command: $closed,    timeout: 5 }],
        PostToolUse:     [{ type: "command", command: $heartbeat, timeout: 5 }],
        UserPromptSubmit:[{ type: "command", command: $heartbeat, timeout: 5 }],
        SubagentStart:   [{ type: "command", command: $subagent,  timeout: 5 }],
        SubagentStop:    [{ type: "command", command: $heartbeat, timeout: 5 }]
    }
}' > "$COPILOT_HOOKS_DIR/session-dashboard.json"

echo "[Copilot]      hooks installed → $COPILOT_HOOKS_DIR/session-dashboard.json"

# ── Done ──────────────────────────────────────────────────────────────────────

echo ""
echo "Hook script: $BASH_SCRIPT"
echo "Restart Claude Code and VS Code for hooks to take effect."
