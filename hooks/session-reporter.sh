#!/usr/bin/env bash
# session-reporter.sh — bash equivalent of session-reporter.ps1
# Requires: jq, curl, git (all standard on Linux/macOS)
# Usage: piped from Claude/Copilot hook stdin, event passed as $1

EVENT="${1:-heartbeat}"

INPUT=$(cat)
[ -z "$INPUT" ] && exit 0

# Claude uses session_id, Copilot uses sessionId
SESSION_ID=$(echo "$INPUT" | jq -r '.session_id // .sessionId // empty' 2>/dev/null)
[ -z "$SESSION_ID" ] && exit 0

CWD=$(echo "$INPUT" | jq -r '.cwd // empty' 2>/dev/null)

# Throttle heartbeats: skip if last POST was <10s ago
IS_HEARTBEAT=false
[[ "$EVENT" == "heartbeat" || "$EVENT" == "subagent-start" ]] && IS_HEARTBEAT=true

THROTTLE_FILE="${TMPDIR:-/tmp}/claude-hook-${SESSION_ID}.txt"
if [ "$IS_HEARTBEAT" = true ] && [ -f "$THROTTLE_FILE" ]; then
    # stat -c on Linux, stat -f on macOS
    MTIME=$(stat -c %Y "$THROTTLE_FILE" 2>/dev/null || stat -f %m "$THROTTLE_FILE" 2>/dev/null || echo 0)
    NOW=$(date +%s)
    (( NOW - MTIME < 10 )) && exit 0
fi
[ "$IS_HEARTBEAT" = true ] && touch "$THROTTLE_FILE"

# Git branch
BRANCH=""
if [ -n "$CWD" ] && command -v git &>/dev/null; then
    BRANCH=$(git -C "$CWD" rev-parse --abbrev-ref HEAD 2>/dev/null || true)
    [ "$BRANCH" = "HEAD" ] && BRANCH=""
fi

# Session name: custom-title > ai-title > cwd basename
SESSION_NAME=$(basename "${CWD:-unknown}")
TRANSCRIPT_PATH=$(echo "$INPUT" | jq -r '.transcript_path // empty' 2>/dev/null)
if [ -n "$TRANSCRIPT_PATH" ] && [ -f "$TRANSCRIPT_PATH" ] && command -v jq &>/dev/null; then
    CUSTOM_TITLE=$(jq -r 'select(.type == "custom-title") | .customTitle' "$TRANSCRIPT_PATH" 2>/dev/null | grep -v null | tail -1 || true)
    AI_TITLE=$(jq -r 'select(.type == "ai-title") | .aiTitle' "$TRANSCRIPT_PATH" 2>/dev/null | grep -v null | tail -1 || true)
    [ -n "$CUSTOM_TITLE" ] && SESSION_NAME="$CUSTOM_TITLE" || { [ -n "$AI_TITLE" ] && SESSION_NAME="$AI_TITLE"; }
fi

BODY=$(jq -n \
    --arg sessionId "$SESSION_ID" \
    --arg name "$SESSION_NAME" \
    --arg event "$EVENT" \
    --argjson pid "$$" \
    --arg workingDir "${CWD:-}" \
    --arg timestamp "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --arg branch "$BRANCH" \
    '{sessionId: $sessionId, name: $name, event: $event, pid: $pid, workingDir: $workingDir, timestamp: $timestamp}
     + (if $branch != "" then {branch: $branch} else {} end)')

curl -sf -X POST \
    -H "Content-Type: application/json" \
    -d "$BODY" \
    --max-time 1 \
    "http://localhost:5900/api/sessions/event" >/dev/null 2>&1 || true
