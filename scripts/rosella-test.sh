#!/bin/bash
# Launches Rosella's compositor on this VT. Any previous instance is stopped
# first — two compositors on one seat steal each other's devices and the
# keyboard dies while the desktop keeps showing.
#
# Runs the repo build (deploy.sh installs the same bits to /usr/local); the
# session needs the input group, hence sg. Logs to /tmp/rosella.log.
set -u

BIN=/home/michael/Projects/Rosella/build/publish/Rosella.Compositor
RUNTIME=/run/user/$(id -u)

for pid in $(pgrep -f "$BIN" || true); do
    echo "rosella-test: stopping previous instance $pid"
    kill "$pid" 2>/dev/null || true
done

# Settle: the control socket must stop answering before the new instance
# starts, or its single-instance check refuses to run.
for _ in $(seq 1 20); do
    pgrep -f "$BIN" >/dev/null 2>&1 || break
    sleep 0.25
done
for pid in $(pgrep -f "$BIN" || true); do
    echo "rosella-test: force-stopping $pid"
    kill -9 "$pid" 2>/dev/null || true
done
sleep 0.5

XDG_RUNTIME_DIR=$RUNTIME sg input -c "$BIN" > /tmp/rosella.log 2>&1
