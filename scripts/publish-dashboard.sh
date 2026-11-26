#!/usr/bin/env bash
# Build against the published compositor and replace only the dashboard widgets.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish="${1:-$root/build/publish}"
if [ ! -f "$publish/Rosella.Compositor.dll" ]; then
    echo "rosella: publish the compositor before building the dashboard widgets" >&2
    exit 1
fi
publish="$(cd "$publish" && pwd)"

stage="$(mktemp -d "$publish/.dashboard-XXXXXX")"
trap 'rm -rf "$stage"' EXIT
"${DOTNET:-dotnet}" build "$root/src/Rosella.DashboardWidgets/Rosella.DashboardWidgets.csproj" \
    -c Release -o "$stage" -p:RosellaHostDirectory="$publish" --nologo
module_dir="$publish/modules/dashboard/v1"
mkdir -p "$module_dir"
mv -f "$stage/Rosella.DashboardWidgets.dll" "$module_dir/Rosella.DashboardWidgets.dll"
echo "Published the dashboard widgets to $module_dir"
