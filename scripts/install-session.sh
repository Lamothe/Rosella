#!/usr/bin/env bash
# Publishes Rosella and installs it as a GDM login session. Requires sudo —
# once. Day-to-day updates need no sudo at all: scripts/deploy.sh publishes
# the build the session launcher prefers, then the session is restarted.
# Remove the session again with: sudo rm /usr/share/wayland-sessions/rosella.desktop \
#   /usr/local/bin/rosella-session /usr/local/bin/rosella-askpass /usr/local/lib/rosella -r
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The SDK may be installed user-local (e.g. ~/.dotnet).
dotnet="${DOTNET:-$(command -v dotnet || true)}"
if [ -z "$dotnet" ] && [ -x "$HOME/.dotnet/dotnet" ]; then
    dotnet="$HOME/.dotnet/dotnet"
fi
if [ -z "$dotnet" ]; then
    echo "rosella: dotnet SDK not found (install .NET 10 or set DOTNET=/path/to/dotnet)" >&2
    exit 1
fi

owner="${SUDO_USER:-$USER}"
if [ "$(id -u)" = 0 ] && [ -n "$SUDO_USER" ]; then
    # Elevated via sudo: build as the invoking user so root never owns
    # build/obj artefacts in the source tree.
    runuser -u "$owner" -- env DOTNET="$dotnet" "$dotnet" publish "$root/src/Rosella.Compositor/Rosella.Compositor.csproj" \
        -c Release --self-contained -o "$root/build/publish" --nologo
    chown -R "$owner:" "$root/build"
else
    "$dotnet" publish "$root/src/Rosella.Compositor/Rosella.Compositor.csproj" \
        -c Release --self-contained -o "$root/build/publish" --nologo
fi

sudo install -d /usr/local/lib/rosella
sudo cp -r "$root/build/publish/." /usr/local/lib/rosella/
sudo install -m 755 "$root/scripts/rosella-session" /usr/local/bin/rosella-session
sudo install -m 755 "$root/scripts/rosella-askpass" /usr/local/bin/rosella-askpass
sudo install -m 644 "$root/scripts/rosella.desktop" /usr/share/wayland-sessions/rosella.desktop

# M6 input: evdev nodes are root:input 660, so the seat user needs the group.
seatUser="$owner"
sudo usermod -aG input "$seatUser"

echo "Installed. 'Rosella' now appears in the GDM session chooser."
echo "Windows, a cursor and keyboard input (Alt+F4 closes, Super+Esc quits)."
echo "Added $seatUser to the 'input' group — log out and back in for it to take effect."
echo "Recovery: Ctrl+Alt+F3 (or another VT), pkill -f Rosella.Compositor (or pick GNOME at the greeter)."
