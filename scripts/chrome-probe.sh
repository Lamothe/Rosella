#!/usr/bin/env bash
# Probe: does Chrome open a window on the Rosella session when launched the
# way the pinned launcher does? Usage: chrome-probe.sh [wayland-display]
set -u
cd ~/Projects/Rosella
D=./build/driver/rosella-driver
export DOTNET_ROOT="$HOME/.dotnet"
WAYLAND_DISPLAY="${1:-wayland-1}"
PROFILE=/tmp/rosella-chrome-probe

# Kill any earlier probe instance (match on the profile path, which never
# appears in *this* script's argv, so pgrep cannot match this shell).
for pid in $(pgrep -f "$PROFILE"); do
    kill "$pid" 2>/dev/null
done
sleep 1

echo "--- surfaces before ---"
"$D" state | head -6

echo "--- launching (wayland ozone) ---"
env WAYLAND_DISPLAY="$WAYLAND_DISPLAY" XDG_RUNTIME_DIR=/run/user/1000 \
    setsid /usr/bin/google-chrome-stable \
    --user-data-dir="$PROFILE" --ozone-platform=wayland \
    --no-first-run --no-default-browser-check >/tmp/chrome-probe.log 2>&1 &
sleep 16

echo "--- surfaces after ---"
"$D" state | head -14
echo "--- chrome log ---"
head -6 /tmp/chrome-probe.log
