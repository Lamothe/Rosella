#!/usr/bin/env python3
"""Run the SDK watcher while the compositor session is alive."""

import os
from pathlib import Path
import pty
import selectors
import signal
import subprocess
import sys
import tempfile
import tty


def main():
    root = Path(__file__).resolve().parent.parent
    project = root / "src/Rosella.Compositor/Rosella.Compositor.csproj"
    stop_signal = None

    def request_stop(signum, _frame):
        nonlocal stop_signal
        stop_signal = signum

    for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(signum, request_stop)

    with tempfile.TemporaryDirectory(prefix="rosella-live-") as directory:
        process_file = Path(directory) / "process"
        env = os.environ | {
            "ROSELLA_LIVE_PROCESS_FILE": str(process_file),
            "DOTNET_WATCH_RESTART_ON_RUDE_EDIT": "0",
            "DOTNET_WATCH_SUPPRESS_EMOJIS": "1",
            "DOTNET_CLI_UI_LANGUAGE": "en-US",
        }
        command = [
            "dotnet", "watch", "run", "--project", str(project),
            "--configuration", "Debug", "--no-launch-profile",
        ]
        if len(sys.argv) > 1:
            command.extend(["--", *sys.argv[1:]])

        # Give the watcher its own input for restart prompts. Rosella still
        # inherits the real controlling VT and opens it through /dev/tty.
        master, slave = pty.openpty()
        try:
            tty.setraw(slave)
            watcher = subprocess.Popen(command, cwd=root, env=env, stdin=slave,
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        finally:
            os.close(slave)

        compositor = None
        pending = b""
        restart_prompt = b"/ Never (v)"
        try:
            with selectors.DefaultSelector() as events:
                events.register(watcher.stdout, selectors.EVENT_READ, "log")
                while watcher.poll() is None:
                    if compositor is None and process_file.exists():
                        pid = process_file.read_text().strip()
                        if pid:
                            try:
                                compositor = os.pidfd_open(int(pid))
                            except ProcessLookupError:
                                break
                            events.register(compositor, selectors.EVENT_READ, "compositor")

                    if stop_signal is not None:
                        if compositor is None:
                            break
                        try:
                            signal.pidfd_send_signal(compositor, stop_signal)
                        except ProcessLookupError:
                            break
                        stop_signal = None

                    ready = events.select(timeout=0.2)
                    if any(key.data == "compositor" for key, _ in ready):
                        break
                    for key, _ in ready:
                        chunk = os.read(key.fd, 8192)
                        if not chunk:
                            events.unregister(key.fileobj)
                            continue
                        sys.stdout.buffer.write(chunk)
                        sys.stdout.buffer.flush()
                        pending = (pending + chunk)[-16384:]
                        if restart_prompt in pending:
                            os.write(master, b"n")
                            pending = b""
        finally:
            if compositor is not None:
                os.close(compositor)
            if watcher.poll() is None:
                watcher.terminate()
            watcher.wait()
            watcher.stdout.close()
            os.close(master)


if __name__ == "__main__":
    main()
