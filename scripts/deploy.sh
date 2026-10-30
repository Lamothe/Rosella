#!/usr/bin/env bash
# Publishes Rosella for the current user and verifies the artefact. No
# sudo: scripts/rosella-session prefers this build, so an update is this
# script plus a session restart. `install-session.sh` remains for the
# one-time GDM registration (which does need sudo).
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

echo "Published to $root/build/publish — restart the Rosella session to pick it up."
