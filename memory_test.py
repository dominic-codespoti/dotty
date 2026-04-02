import os, socket, subprocess, time


def rss_mb(pid):
    with open(f"/proc/{pid}/status", "r") as f:
        for line in f:
            if line.startswith("VmRSS:"):
                return int(line.split()[1]) / 1024.0


def test_memory(port, command_name, create_fn):
    env = os.environ.copy()
    env["DOTTY_TEST_PORT"] = str(port)
    proc = subprocess.Popen(
        [
            "/home/dom/projects/dotnet-term/src/Dotty.App/bin/Release/net10.0/linux-x64/publish/Dotty.App"
        ],
        cwd="/home/dom/projects/dotnet-term",
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    def send(cmd):
        with socket.create_connection(("127.0.0.1", port), timeout=5) as s:
            s.sendall((cmd + "\n").encode())
            return s.recv(1024).decode().strip()

    try:
        # Wait for ready
        deadline = time.time() + 20
        while time.time() < deadline:
            try:
                send("PREV_TAB")
                break
            except:
                time.sleep(0.2)

        start_rss = rss_mb(proc.pid)

        # Create 20 tabs
        for _ in range(20):
            send(create_fn())

        time.sleep(1)  # Let things settle
        after_create = rss_mb(proc.pid)

        # Idle for 10 seconds (let inactive timer fire)
        time.sleep(12)
        after_idle = rss_mb(proc.pid)

        print(f"{command_name}:")
        print(f"  Start RSS: {start_rss:.1f} MB")
        print(
            f"  After 20 tabs: {after_create:.1f} MB (+{after_create - start_rss:.1f} MB)"
        )
        print(
            f"  After 10s idle: {after_idle:.1f} MB (+{after_idle - start_rss:.1f} MB)"
        )

    finally:
        proc.terminate()
        proc.wait(timeout=5)


# Test both modes
test_memory(10500, "Eager (NEW_TAB)", lambda: "NEW_TAB")
print()
test_memory(10501, "Lazy (NEW_TAB_BG)", lambda: "NEW_TAB_BG")
