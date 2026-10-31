#!/usr/bin/env bash
# Publishes Rosella for the current user and verifies the artefact. No
# sudo: scripts/rosella-session prefers this build, so an update is this
# script plus a session restart. `install-session.sh` remains for the
# one-time GDM registration (which does need sudo).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Both login and VT launchers use this location, regardless of where the
# deploy script was invoked. Do not report an update from another checkout.
expected_root="$HOME/Projects/Rosella"
if [ ! -d "$expected_root" ] || [ ! "$root" -ef "$expected_root" ]; then
    echo "rosella: both launchers use $expected_root/build/publish; run deploy.sh from that checkout" >&2
    exit 1
fi

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

# The artefact must stand alone: the session runs it on machines without
# the SDK. A missing libcoreclr.so means the publish was not
# self-contained and the session would fail on start.
if [ ! -f "$root/build/publish/libcoreclr.so" ]; then
    echo "rosella: publish is not self-contained (no libcoreclr.so); refusing" >&2
    exit 1
fi

if [ ! -x "$root/build/publish/Rosella.Compositor" ]; then
    echo "rosella: compositor binary missing from the publish" >&2
    exit 1
fi

# The dev launcher (scripts/rosella-dev.sh) is the VT entry point; put it on
# PATH as `rosella` so the workflow is just "run rosella". No sudo: it lives
# in the user's own ~/.local/bin.
mkdir -p "$HOME/.local/bin"
ln -sf "$root/scripts/rosella-dev.sh" "$HOME/.local/bin/rosella"

# GDM uses a separately installed wrapper. A newer user build alone cannot
# fix an old wrapper that still launches the system copy. Warn after a
# successful publish without blocking the VT-only workflow.
installed_session=/usr/local/bin/rosella-session
installed_desktop=/usr/share/wayland-sessions/rosella.desktop
if [ -e "$installed_session" ] || [ -e "$installed_desktop" ]; then
    if [ ! -x "$installed_session" ] || [ ! -r "$installed_desktop" ] \
        || ! cmp -s "$root/scripts/rosella-session" "$installed_session" \
        || ! cmp -s "$root/scripts/rosella.desktop" "$installed_desktop"; then
        echo "WARNING: the installed Rosella login launcher or session entry differs from this repository." >&2
        echo "The GDM login may use an old build; refresh the registration with scripts/install-session.sh." >&2
    fi
fi

echo "Published to $root/build/publish — restart the Rosella session to pick it up."
echo "Run it from a VT with: rosella"
