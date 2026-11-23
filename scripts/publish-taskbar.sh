#!/usr/bin/env bash
# Build against the published compositor and replace only the taskbar module.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish="${1:-$root/build/publish}"
if [ ! -f "$publish/Rosella.Compositor.dll" ]; then
    echo "rosella: publish the compositor before building the taskbar module" >&2
    exit 1
fi
publish="$(cd "$publish" && pwd)"

stage="$(mktemp -d "$publish/.taskbar-XXXXXX")"
trap 'rm -rf "$stage"' EXIT
"${DOTNET:-dotnet}" build "$root/src/Rosella.Taskbar/Rosella.Taskbar.csproj" \
    -c Release -o "$stage" -p:RosellaHostDirectory="$publish" --nologo
module_dir="$publish/modules/v2"
mkdir -p "$module_dir"
mv -f "$stage/Rosella.Taskbar.dll" "$module_dir/Rosella.Taskbar.dll"
echo "Published the taskbar module to $module_dir"
