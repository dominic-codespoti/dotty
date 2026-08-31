#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
STATE_DIR="${DOTTY_TEST_STATE_DIR:-${TMPDIR:-/tmp}/dotty-interact-${USER:-unknown}}"
PORT_FILE="$STATE_DIR/port"
PID_FILE="$STATE_DIR/pid"
APP_LOG="$STATE_DIR/app.log"
PYTHON_BIN="${DOTTY_TEST_PYTHON:-python3}"
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1 && command -v python >/dev/null 2>&1; then
    PYTHON_BIN=python
fi
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
    echo "ERROR: Python 3 is required by dotty-interact.sh." >&2
    exit 1
fi
usage() {
    cat <<'USAGE'
Usage: dotty-interact.sh <command> [args]

Launch, interact with, and inspect the Dotty terminal emulator.

Commands:
  launch                 Build (if needed) and start Dotty headlessly
  type <text>            Send text to the active terminal
  key <keyname>          Send a special key (Enter, Tab, CtrlC, Escape, Backspace)
  dump                   Dump the terminal screen as ANSI-colored text
  screenshot             Capture a screenshot and convert to ASCII art
  state                  Show terminal state (cursor, dimensions)
  send <raw>             Send an arbitrary TCP command to the app
  close                  Shut down the application
  wait                   Wait for command port to become ready

Examples:
  dotty-interact.sh launch
  dotty-interact.sh type "ls -la"
  dotty-interact.sh key Enter
  dotty-interact.sh dump
  dotty-interact.sh screenshot
  dotty-interact.sh state
  dotty-interact.sh close
USAGE
}

get_port() {
    if [ ! -f "$PORT_FILE" ]; then
        echo "ERROR: No port file found. Run 'launch' first." >&2
        return 1
    fi
    local port
    port=$(cat "$PORT_FILE")
    if [[ ! "$port" =~ ^[0-9]+$ ]]; then
        echo "ERROR: Invalid port file: $PORT_FILE" >&2
        return 1
    fi
    printf '%s\n' "$port"
}

send_tcp() {
    local command="$1"
    local port
    port=$(get_port)

    if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
        echo "ERROR: Python is required for the cross-platform control transport." >&2
        return 1
    fi

    "$PYTHON_BIN" - "$port" "$command" <<'PY'
import socket
import sys

port = int(sys.argv[1])
command = sys.argv[2]
with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
    connection.sendall((command + "\n").encode("utf-8"))
    chunks = []
    while True:
        chunk = connection.recv(65536)
        if not chunk:
            break
        chunks.append(chunk)
sys.stdout.write(b"".join(chunks).decode("utf-8", "replace"))
PY
}

port_ready() {
    local port="$1"
    "$PYTHON_BIN" - "$port" 2>/dev/null <<'PY'
import socket
import sys

with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=0.5):
    pass
PY
}

build_app() {
    echo "=== Building Dotty ==="
    mkdir -p "$STATE_DIR"
    local helper="$PROJECT_ROOT/src/Dotty.NativePty/bin/pty-helper"
    if [ ! -x "$helper" ]; then
        echo "Building PTY helper..."
        make -C "$PROJECT_ROOT/src/Dotty.NativePty"
    fi
    dotnet build "$PROJECT_ROOT/src/Dotty/Dotty.csproj" -c Debug --nologo --verbosity quiet
    echo "Build complete."
}

launch_app() {
    mkdir -p "$STATE_DIR"
    if [ -f "$PID_FILE" ]; then
        local old_pid
        old_pid=$(cat "$PID_FILE")
        if [[ "$old_pid" =~ ^[0-9]+$ ]] && kill -0 "$old_pid" 2>/dev/null; then
            echo "ERROR: An existing Dotty process is tracked by $PID_FILE; run 'close' first." >&2
            return 1
        fi
    fi
    rm -f "$PORT_FILE" "$PID_FILE" "$APP_LOG"

    build_app

    local port
    port=$("$PYTHON_BIN" -c "import socket; s=socket.socket(); s.bind(('127.0.0.1', 0)); print(s.getsockname()[1]); s.close()")
    local app_home="$STATE_DIR/home"
    mkdir -p "$app_home"

    echo "=== Launching Dotty on port $port ==="
    echo "$port" > "$PORT_FILE"

    local -a app_command
    app_command=(dotnet run --project "$PROJECT_ROOT/src/Dotty/Dotty.csproj" -c Debug --no-build --no-restore)
    if command -v xvfb-run >/dev/null 2>&1; then
        env HOME="$app_home" XDG_CONFIG_HOME="$app_home/.config" \
            DOTTY_CONFIG_HOME="$app_home/.config/dotty" DOTTY_TEST_PORT="$port" \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 xvfb-run -a "${app_command[@]}" > "$APP_LOG" 2>&1 &
    else
        env HOME="$app_home" XDG_CONFIG_HOME="$app_home/.config" \
            DOTTY_CONFIG_HOME="$app_home/.config/dotty" DOTTY_TEST_PORT="$port" \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 "${app_command[@]}" > "$APP_LOG" 2>&1 &
    fi

    local pid=$!
    echo "$pid" > "$PID_FILE"
    echo "Waiting for app to become ready..."
    local waited=0
    while [ "$waited" -lt 60 ]; do
        if port_ready "$port"; then
            echo "App is ready on port $port (PID: $pid)"
            return 0
        fi
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "ERROR: Dotty exited before opening its control port." >&2
            cat "$APP_LOG" >&2
            return 1
        fi
        sleep 0.5
        waited=$((waited + 1))
    done

    echo "ERROR: App failed to start within 30 seconds" >&2
    cat "$APP_LOG" >&2
    return 1
}

cmd_type() {
    local text="$1"
    echo "TYPING: $text"
    local response
    response=$(send_tcp "TYPE:$text")
    printf '%s\n' "$response"
    [[ "$response" == OK* ]]
}

cmd_key() {
    local keyname="$1"
    echo "KEY: $keyname"
    local response
    response=$(send_tcp "KEY:$keyname")
    printf '%s\n' "$response"
    [[ "$response" == OK* ]]
}

cmd_dump() {
    local raw
    raw=$(send_tcp "DUMP")

    # Check for special responses
    if echo "$raw" | grep -q "^DUMP EMPTY"; then
        echo "=== TERMINAL SCREEN ==="
        echo "(no active terminal session)"
        return 0
    fi

    # Parse multi-line DUMP response
    local rows=""
    local cols=""
    local cur_row=""
    local cur_col=""
    local in_lines=false
    local line_num=0

    echo "=== TERMINAL SCREEN ==="

    while IFS= read -r line; do
        # Skip empty lines
        [ -z "$line" ] && continue

        # Parse header
        if [[ "$line" =~ ^R=([0-9]+)\ C=([0-9]+)\ CUR=([0-9]+),([0-9]+)$ ]]; then
            rows="${BASH_REMATCH[1]}"
            cols="${BASH_REMATCH[2]}"
            cur_row="${BASH_REMATCH[3]}"
            cur_col="${BASH_REMATCH[4]}"
            echo "Dimensions: ${rows}x${cols}  Cursor: (${cur_row},${cur_col})"
            echo "---"
            in_lines=true
            continue
        fi

        # End of dump
        [ "$line" = "END" ] && continue
        [ "$line" = "DUMP OK" ] && continue

        # Display the line
        if $in_lines; then
            # Pass through ANSI escape sequences raw (already ESC bytes, not \escapes)
            printf '%s\n' "$line"
            line_num=$((line_num + 1))
        fi
    done <<< "$raw"

    echo "---"
    echo "$line_num lines shown"
}

cmd_screenshot() {
    if ! command -v xdotool &>/dev/null; then
        echo "WARNING: xdotool not found. Install with: sudo apt install xdotool"
        echo "Falling back to screen area capture..."
    fi
    if ! command -v import &>/dev/null; then
        echo "WARNING: imagemagick (import) not found. Install with: sudo apt install imagemagick"
        echo "Skipping screenshot."
        return 1
    fi
    if ! command -v chafa &>/dev/null; then
        echo "WARNING: chafa not found. Install with: sudo apt install chafa"
        echo "Skipping ASCII art conversion."
        return 1
    fi

    local png_path="/tmp/dotty-screenshot.png"

    # Try to capture the Dotty window
    local win_id
    win_id=$(xdotool search --name "Dotty" 2>/dev/null | head -1)

    if [ -n "$win_id" ]; then
        echo "Capturing Dotty window (ID: $win_id)..."
        import -window "$win_id" "$png_path" 2>/dev/null || {
            echo "Window capture failed. Trying root window..."
            import -window root "$png_path" 2>/dev/null || {
                echo "Screenshot capture failed."
                return 1
            }
        }
    else
        echo "Dotty window not found via xdotool. Trying root window capture..."
        import -window root "$png_path" 2>/dev/null || {
            echo "Screenshot capture failed."
            return 1
        }
    fi

    local size
    size=$(stat -c%s "$png_path" 2>/dev/null || echo "0")
    if [ "$size" -lt 100 ]; then
        echo "Screenshot too small (${size}B), might be blank."
    else
        echo "Screenshot captured (${size}B)"
    fi

    echo ""
    echo "=== ASCII ART ==="
    chafa --symbols solid --size 80x24 "$png_path" 2>/dev/null || {
        echo "(chafa conversion failed)"
    }
}

cmd_state() {
    local raw
    raw=$(send_tcp "GET_STATE")

    if command -v "$PYTHON_BIN" >/dev/null 2>&1; then
        RAW_RESPONSE="$raw" "$PYTHON_BIN" - <<'PY'
import json
import os

raw = os.environ["RAW_RESPONSE"].lstrip("\ufeff")
try:
    state = json.loads(raw)
    print("Terminal State:")
    print(f"  Dimensions: {state.get('rows', '?')}x{state.get('cols', '?')}")
    print(f"  Cursor: ({state.get('cursorRow', '?')},{state.get('cursorCol', '?')})")
    print(f"  Scrollback: {state.get('scrollbackLines', '?')} lines")
    print(f"  Alternate Screen: {state.get('isAlternateScreen', '?')}")
    print(f"  Title: {state.get('title', '')}")
except (TypeError, ValueError) as error:
    print("Raw response:", raw)
    print("Parse error:", error)
PY
    else
        printf '%s\n' "$raw"
    fi
}

cmd_send() {
    local raw
    raw=$(send_tcp "$1")
    echo "$raw"
}

cmd_close() {
    echo "=== Shutting down Dotty ==="
    local port=""
    local pid=""

    if [ -f "$PORT_FILE" ]; then
        port=$(get_port)
        if port_ready "$port"; then
            local response
            if response=$(send_tcp "SHUTDOWN"); then
                printf '%s\n' "$response"
            else
                echo "WARNING: Control shutdown request failed; using the tracked process." >&2
            fi
        fi
    fi

    if [ -f "$PID_FILE" ]; then
        pid=$(cat "$PID_FILE")
        if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
            echo "Waiting for tracked Dotty process $pid to exit..."
            for _ in $(seq 1 25); do
                if ! kill -0 "$pid" 2>/dev/null; then break; fi
                sleep 0.2
            done
            if kill -0 "$pid" 2>/dev/null; then
                echo "Graceful shutdown incomplete, sending SIGTERM to PID $pid..."
                kill "$pid"
                for _ in $(seq 1 25); do
                    if ! kill -0 "$pid" 2>/dev/null; then break; fi
                    sleep 0.2
                done
            fi
            if kill -0 "$pid" 2>/dev/null; then
                echo "Sending SIGKILL to tracked PID $pid..."
                kill -KILL "$pid"
                sleep 0.5
            fi
            if kill -0 "$pid" 2>/dev/null; then
                echo "ERROR: Tracked Dotty process $pid did not exit." >&2
                return 1
            fi
        fi
    fi

    rm -f "$PORT_FILE" "$PID_FILE"
    echo "Done."
}

# Main
if [ $# -eq 0 ]; then
    usage
    exit 0
fi

CMD="${1:-help}"
shift

case "$CMD" in
    launch)
        launch_app
        ;;
    type)
        [ $# -ge 1 ] || { echo "Usage: dotty-interact.sh type <text>"; exit 1; }
        cmd_type "$1"
        ;;
    key)
        [ $# -ge 1 ] || { echo "Usage: dotty-interact.sh key <keyname>"; exit 1; }
        cmd_key "$1"
        ;;
    dump)
        cmd_dump
        ;;
    screenshot)
        cmd_screenshot
        ;;
    state)
        cmd_state
        ;;
    send)
        [ $# -ge 1 ] || { echo "Usage: dotty-interact.sh send <raw-command>"; exit 1; }
        cmd_send "$1"
        ;;
    close)
        cmd_close
        ;;
    pause)
        sleep "${1:-1}"
        ;;
    wait)
        port=$(get_port)
        waited=0
        while [ "$waited" -lt 60 ]; do
            if port_ready "$port"; then
                echo "READY"
                exit 0
            fi
            sleep 0.5
            waited=$((waited + 1))
        done
        echo "TIMEOUT" >&2
        exit 1
        ;;
    help|--help|-h)
        usage
        ;;
    *)
        echo "Unknown command: $CMD"
        usage
        exit 1
        ;;
esac
