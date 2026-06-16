#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
PORT_FILE="/tmp/dotty-test-port"
PID_FILE="/tmp/dotty-test-pid"

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
        exit 1
    fi
    cat "$PORT_FILE"
}

send_tcp() {
    local command="$1"
    local port
    port=$(get_port)

    if command -v nc &>/dev/null; then
        # Use nc with 2-second timeout
        printf '%s\n' "$command" | nc -w 2 "127.0.0.1" "$port" 2>/dev/null || true
    else
        # Fallback: use bash /dev/tcp
        exec 3<>"/dev/tcp/127.0.0.1/$port"
        printf '%s\n' "$command" >&3
        local line
        while IFS= read -r line <&3; do
            echo "$line"
        done
        exec 3>&-
    fi
}

build_app() {
    echo "=== Building Dotty ==="
    if [ ! -f "$PROJECT_ROOT/src/Dotty.NativePty/pty-helper" ]; then
        echo "Building PTY helper..."
        make -C "$PROJECT_ROOT/src/Dotty.NativePty" 2>&1 || echo "WARNING: PTY helper build failed. Trying dotnet build anyway..."
    fi
    dotnet build "$PROJECT_ROOT/src/Dotty.App/Dotty.App.csproj" -c Debug --verbosity quiet 2>&1 || true
    echo "Build complete."
}

launch_app() {
    # Clean stale state
    rm -f "$PORT_FILE" "$PID_FILE"

    build_app

    local port
    # Find a free port by trying binds
    port=$(python3 -c "
import socket
s = socket.socket()
s.bind(('127.0.0.1', 0))
print(s.getsockname()[1])
s.close()
" 2>/dev/null) || port=$((20000 + RANDOM % 10000))

    echo "=== Launching Dotty on port $port ==="
    echo "$port" > "$PORT_FILE"

    local use_xvfb=false
    if command -v xvfb-run &>/dev/null; then
        use_xvfb=true
    fi

    if $use_xvfb; then
        DOTTY_TEST_PORT="$port" xvfb-run -a dotnet run --project "$PROJECT_ROOT/src/Dotty.App" --no-build &
    else
        DOTTY_TEST_PORT="$port" dotnet run --project "$PROJECT_ROOT/src/Dotty.App" --no-build &
    fi

    local pid=$!
    echo "$pid" > "$PID_FILE"

    echo "Waiting for app to become ready..."
    local waited=0
    while [ $waited -lt 30 ]; do
        if nc -z 127.0.0.1 "$port" 2>/dev/null; then
            echo "App is ready on port $port (PID: $pid)"
            return 0
        fi
        sleep 1
        waited=$((waited + 1))
    done

    echo "ERROR: App failed to start within 30 seconds"
    rm -f "$PORT_FILE" "$PID_FILE"
    return 1
}

cmd_type() {
    local text="$1"
    echo "TYPING: $text"
    send_tcp "TYPE:$text"
    echo "OK"
}

cmd_key() {
    local keyname="$1"
    echo "KEY: $keyname"
    send_tcp "KEY:$keyname"
    echo "OK"
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

    # Strip BOM if present
    raw="${raw#"$(printf '\xEF\xBB\xBF')"}"
    raw="${raw#"$(printf '\xFE\xFF')"}"
    raw="${raw#"$(printf '\xFF\xFE')"}"

    if command -v python3 &>/dev/null; then
        python3 -c "
import json, sys
raw = '''$raw'''
try:
    d = json.loads(raw)
    print('Terminal State:')
    print(f'  Dimensions: {d.get(\"rows\",\"?\")}x{d.get(\"cols\",\"?\")}')
    print(f'  Cursor: ({d.get(\"cursorRow\",\"?\")},{d.get(\"cursorCol\",\"?\")})')
    print(f'  Scrollback: {d.get(\"scrollbackLines\",\"?\")} lines')
    print(f'  Alternate Screen: {d.get(\"isAlternateScreen\",\"?\")}')
    print(f'  Title: {d.get(\"title\",\"\")}')
except Exception as e:
    print('Raw response:', raw)
    print('Parse error:', e)
" 2>/dev/null || echo "Raw response: $raw"
    else
        echo "$raw"
    fi
}

cmd_send() {
    local raw
    raw=$(send_tcp "$1")
    echo "$raw"
}

cmd_close() {
    echo "=== Shutting down Dotty ==="

    # 1. Try graceful shutdown via TCP
    local port=""
    if [ -f "$PORT_FILE" ]; then
        port=$(cat "$PORT_FILE")
        printf 'SHUTDOWN\n' | nc -w 2 127.0.0.1 "$port" 2>/dev/null || true
        sleep 2
    fi

    # 2. If still running, kill via PID file
    local pid=""
    if [ -f "$PID_FILE" ]; then
        pid=$(cat "$PID_FILE")
        if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
            echo "Graceful shutdown incomplete, sending SIGTERM to PID $pid..."
            kill "$pid" 2>/dev/null || true
            sleep 2
            if kill -0 "$pid" 2>/dev/null; then
                echo "Sending SIGKILL..."
                kill -9 "$pid" 2>/dev/null || true
            fi
        fi
    fi

    # 3. Last resort: force kill any remaining Dotty processes
    if [ -n "$port" ] && nc -z 127.0.0.1 "$port" 2>/dev/null; then
        echo "Process still listening, force killing..."
        for proc_dir in /proc/[0-9]*/; do
            [ -d "$proc_dir" ] || continue
            local cfile="${proc_dir}cmdline"
            [ -f "$cfile" ] || continue
            if grep -q "Dotty\.App" "$cfile" 2>/dev/null; then
                proc_pid="${proc_dir%/}"
                proc_pid="${proc_pid##*/}"
                kill -9 "$proc_pid" 2>/dev/null || true
            fi
        done
        sleep 1
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
shift || true

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
        local port
        port=$(get_port)
        local waited=0
        while [ $waited -lt 30 ]; do
            if nc -z 127.0.0.1 "$port" 2>/dev/null; then
                echo "READY"
                exit 0
            fi
            sleep 1
            waited=$((waited + 1))
        done
        echo "TIMEOUT"
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
