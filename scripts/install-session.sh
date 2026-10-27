#!/usr/bin/env bash
# Publishes Rosella and installs it as a GDM login session. Requires sudo.
# Remove the session again with: sudo rm /usr/share/wayland-sessions/rosella.desktop \
#   /usr/local/bin/rosella-session /usr/local/lib/rosella -r
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

"$dotnet" publish "$root/src/Rosella.Compositor/Rosella.Compositor.csproj" \
    -c Release --self-contained -o "$root/build/publish" --nologo

sudo install -d /usr/local/lib/rosella
sudo cp -r "$root/build/publish/." /usr/local/lib/rosella/
sudo install -m 755 "$root/scripts/rosella-session" /usr/local/bin/rosella-session
sudo install -m 644 "$root/scripts/rosella.desktop" /usr/share/wayland-sessions/rosella.desktop

echo "Installed. 'Rosella' now appears in the GDM session chooser."
echo "Selecting it drives the first connected output (test pattern) until milestone 5."
echo "Recovery: Ctrl+Alt+F3, log in, pkill -f Rosella.Compositor (or pick GNOME at the greeter)."
