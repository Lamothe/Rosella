#!/usr/bin/env bash
# Probe 2: does XDG_SESSION_TYPE=wayland alone (no --ozone-platform flag)
# make Chromium-family apps pick the Wayland socket? That would be the one
# generic fix for every toolkit, rather than per-app argv surgery.
# Usage: session-type-probe.sh <wayland-display>
set -u
cd ~/Projects/Rosella
D=./build/driver/rosella-driver
export DOTNET_ROOT="$HOME/.dotnet"
WAYLAND_DISPLAY="${1:-wayland-1}"
PROFILE=/tmp/rosella-session-type-probe

for pid in $(pgrep -f "$PROFILE"); do
    kill "$pid" 2>/dev/null
done
sleep 1

echo "--- current XDG_SESSION_TYPE ---"
echo "XDG_SESSION_TYPE=[$XDG_SESSION_TYPE]"
echo "--- surfaces before ---"
"$D" state | grep -c "mapped=True" || true

echo "--- launching chrome: session-type only, NO ozone flag ---"
env WAYLAND_DISPLAY="$WAYLAND_DISPLAY" \
    XDG_RUNTIME_DIR=/run/user/1000 XDG_SESSION_TYPE=wayland \
    setsid /usr/bin/google-chrome-stable \
    --user-data-dir="$PROFILE" --no-first-run --no-default-browser-check \
    >/tmp/session-type-probe.log 2>&1 &
sleep 16

echo "--- surfaces after (mapped=True count) ---"
"$D" state | grep "mapped=True" | head -4
echo "--- count ---"
"$D" state | grep -c "mapped=True" || true
echo "--- chrome log head ---"
grep -iE "ozone|platform|Missing X|wayland" /tmp/session-type-probe.log | head -5
