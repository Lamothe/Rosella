#!/usr/bin/env bash
# Runs Rosella on this VT right now, without logging out of GNOME.
#
# GDM only allows one session per user, so you cannot pick "Rosella" at the
# greeter while GNOME is running. This is the flip-to-a-VT-and-go path; the
# proper login is "Rosella" in the GDM session chooser (rosella.desktop ->
# rosella-session). Both run the same build.
#
# Any previous instance is stopped first: two compositors on one seat steal
# each other's input devices and the keyboard dies while the desktop keeps
# rendering. That is a hard failure mode, not a nuisance — never bypass this.
#
# Runs the repo build (scripts/deploy.sh publishes it; install-session.sh
# copies the same bits to /usr/local). Needs the input group, hence sg.
# Appends to /tmp/rosella.log so a crash survives into the next run.
set -u

BIN="$HOME/Projects/Rosella/build/publish/Rosella.Compositor"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export XDG_CURRENT_DESKTOP=Rosella
export XDG_SESSION_TYPE=wayland

if [ ! -x "$BIN" ]; then
    echo "rosella-dev: no build at $BIN" >&2
    echo "rosella-dev: run ~/Projects/Rosella/scripts/deploy.sh first" >&2
    exit 1
fi

for pid in $(pgrep -f "$BIN" || true); do
    echo "rosella-dev: stopping previous instance $pid"
    kill "$pid" 2>/dev/null || true
done

# Settle: the control socket must stop answering before the new instance
# starts, or its single-instance check refuses to run.
for _ in $(seq 1 20); do
    pgrep -f "$BIN" >/dev/null 2>&1 || break
    sleep 0.25
done
for pid in $(pgrep -f "$BIN" || true); do
    echo "rosella-dev: force-stopping $pid"
    kill -9 "$pid" 2>/dev/null || true
done
sleep 0.5

# Cap the log so months of appends do not grow without bound.
if [ -f /tmp/rosella.log ] && [ "$(stat -c %s /tmp/rosella.log 2>/dev/null || echo 0)" -gt 5242880 ]; then
    : > /tmp/rosella.log
fi

echo "rosella-dev: starting $(date '+%d/%m/%Y %H:%M:%S') ($BIN)" >> /tmp/rosella.log

if id -nG | tr ' ' '\n' | grep -qx input; then
    exec "$BIN" >> /tmp/rosella.log 2>&1
fi
exec sg input -c "$BIN" >> /tmp/rosella.log 2>&1
