#!/usr/bin/env bash
# Publishes Rosella and installs it as a GDM login session. Requires sudo.
# Remove the session again with: sudo rm /usr/share/wayland-sessions/rosella.desktop \
#   /usr/local/bin/rosella-session /usr/local/lib/rosella -r
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet publish "$root/src/Rosella.Compositor/Rosella.Compositor.csproj" \
    -c Release --self-contained -o "$root/build/publish" --nologo

sudo install -d /usr/local/lib/rosella
sudo cp -r "$root/build/publish/." /usr/local/lib/rosella/
sudo install -m 755 "$root/scripts/rosella-session" /usr/local/bin/rosella-session
sudo install -m 644 "$root/scripts/rosella.desktop" /usr/share/wayland-sessions/rosella.desktop

echo "Installed. 'Rosella' now appears in the GDM session chooser."
echo "WARNING: until milestone 3 lands output, selecting it gives a blank screen."
echo "Recovery: Ctrl+Alt+F3, log in, pkill -f Rosella.Compositor (or pick GNOME at the greeter)."
