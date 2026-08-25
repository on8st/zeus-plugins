#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Build the installable plugin package.
#
# Format, read from PluginInstaller.InstallFromZipFileAsync: a .zip with
# plugin.json AT THE TOP LEVEL — the installer does GetEntry("plugin.json") and
# refuses the package outright if it is nested inside a folder, which is what
# `zip -r pkg.zip mydir/` produces and is the easy mistake here.
#
# Two things must NOT be in it. Zeus.Plugins.Contracts.dll: the host forces the
# contracts to resolve from its own load context, so a shipped copy gives the
# interface types two identities and the plugin fails to bind. And the .pdb,
# which is just weight.
#
# The result installs three ways: the "install local feature" file picker in the
# Zeus UI, POST /api/plugins/install/zip (multipart), or
# POST /api/plugins/install with {"source":"file","filePath":"..."}.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$HERE/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT_DIR="${1:-$PLUGIN_DIR/dist}"

"$DOTNET" build "$PLUGIN_DIR/src/Zeus.Plugin.Ubersdr/Zeus.Plugin.Ubersdr.csproj" -c Release >/dev/null

BUILD="$PLUGIN_DIR/src/Zeus.Plugin.Ubersdr/bin/Release/net10.0"
VERSION="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["version"])' "$BUILD/plugin.json")"
ID="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["id"])' "$BUILD/plugin.json")"

mkdir -p "$OUT_DIR"
ZIP="$OUT_DIR/$ID-$VERSION.zip"
rm -f "$ZIP"

# cd into the build output so paths in the archive are relative to it — this is
# what puts plugin.json at the top level rather than under a directory.
(cd "$BUILD" && zip -qr "$ZIP" . -x "Zeus.Plugins.Contracts.dll" "*.pdb")

# Render the panel before shipping it. `node --check` only parses, and the bug
# that got past it — a hook whose dependency array names a const declared later
# in the component — is a ReferenceError on first render, which reaches the
# operator as "one of the panels failed to render" and nothing else.
if command -v node >/dev/null 2>&1; then
  # Every panel module, not just *.es.js — a second panel added under another
  # name would otherwise ship unchecked, which is exactly what happened.
  # vendor/ is excluded: those are libraries, not panels.
  for panel in "$BUILD"/ui/*.js; do
    [ -e "$panel" ] || continue
    grep -q "export default function register" "$panel" || continue
    node "$PLUGIN_DIR/tools/panel-check/check.mjs" "$panel" || {
      echo "ERROR: the panel does not render; refusing to package" >&2; exit 1; }
  done
else
  echo "warning: node not installed; the panel was not render-checked" >&2
fi

# Fail loudly rather than shipping a package the installer will reject.
unzip -l "$ZIP" | awk '{print $4}' | grep -qx "plugin.json" \
  || { echo "ERROR: plugin.json is not at the top level of $ZIP" >&2; exit 1; }
unzip -l "$ZIP" | awk '{print $4}' | grep -qx "Zeus.Plugins.Contracts.dll" \
  && { echo "ERROR: the contracts assembly must not be shipped" >&2; exit 1; }

echo "$ZIP"
echo "  sha256 $(shasum -a 256 "$ZIP" | cut -d' ' -f1)"
unzip -l "$ZIP" | awk 'NR>3 && NF>3 && $4!="" {print "  "$4}'
