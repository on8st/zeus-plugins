#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Build the degraded SDK 1.4.0 package — the one that will actually load on a
# released engine.
#
# Why this exists. The host forces the contracts to resolve from its own load
# context, so a plugin compiled against 1.5.0 contracts and dropped onto a
# 1.4.0 host does not "mostly work": ITxTelemetry and TxFrame are not there,
# and it fails to bind. Editing minVersion in plugin.json therefore gets you a
# package that installs and then breaks, which is worse than one that refuses.
#
# So this build genuinely targets 1.4.0:
#   - TxBridge.Legacy.cs replaces TxBridge.Full.cs (ZeusSdk14=true), and it
#     names nothing the old contracts lack
#   - it compiles against a real 1.4.0 checkout, so the compiler is the proof
#     rather than a promise
#   - the manifest's minVersion is rewritten to 1.4.0 in the build output, not
#     in the source, so the shipped plugin.json stays honest
#
# What you get: the panel renders, polls, and reports Unknown with blank meters.
# MOX is the one control that reaches the radio. That is enough to prove the
# panel, the routes and the manifest wire up, and nothing more.
#
# Usage: ./tools/package-sdk14.sh [out-dir]
#   ENGINE_REPO   station-engine clone to take 1.4.0 from (default: sibling)
#   SDK14_REF     ref holding the 1.4.0 contracts   (default: origin/main)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$HERE/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT_DIR="${1:-$PLUGIN_DIR/dist}"
ENGINE_REPO="${ENGINE_REPO:-$PLUGIN_DIR/../../station-engine}"
SDK14_REF="${SDK14_REF:-origin/main}"
WORKTREE="${TMPDIR:-/tmp}/zeus-sdk14-worktree"

[ -d "$ENGINE_REPO/.git" ] || {
  echo "ERROR: no station-engine clone at $ENGINE_REPO (set ENGINE_REPO)" >&2; exit 2; }

# A detached worktree at the released ref, so the 1.4.0 contracts are the real
# ones and not a hand-edited copy.
# Prune first: a temp dir cleared by the OS leaves the worktree registered,
# and `add` then refuses a path git still believes in.
git -C "$ENGINE_REPO" worktree prune
if [ ! -d "$WORKTREE" ]; then
  git -C "$ENGINE_REPO" worktree add -q --detach "$WORKTREE" "$SDK14_REF"
fi

SDK_IN_WORKTREE="$(grep -o '"[0-9]\+\.[0-9]\+\.[0-9]\+"' \
  "$WORKTREE/Zeus.Plugins.Contracts/AbiVersion.cs" | tr -d '"' | head -1)"
[ "$SDK_IN_WORKTREE" = "1.4.0" ] || {
  echo "ERROR: $SDK14_REF carries SDK $SDK_IN_WORKTREE, not 1.4.0" >&2; exit 1; }

# A named configuration rather than -p:BaseOutputPath: overriding the output
# paths on the command line propagates into the referenced contracts project
# and collides with its own obj, which fails as duplicate AssemblyInfo.
"$DOTNET" build "$PLUGIN_DIR/src/Zeus.Plugin.Simpletx/Zeus.Plugin.Simpletx.csproj" \
  -c Sdk14 -p:ZeusSdk14=true -p:ZeusEngineRoot="$WORKTREE" >/dev/null

BUILD="$PLUGIN_DIR/src/Zeus.Plugin.Simpletx/bin/Sdk14/net10.0"

# Rewrite minVersion in the build output only. The source manifest keeps
# declaring 1.5.0, because that is what the full plugin actually needs.
python3 - "$BUILD/plugin.json" <<'PY'
import json, sys
path = sys.argv[1]
with open(path) as fh:
    manifest = json.load(fh)
manifest["sdk"]["minVersion"] = "1.4.0"
manifest["description"] = (
    "Degraded build for SDK 1.4.0 hosts: the panel renders and polls, but the "
    "radio's transmit path is not reachable through these contracts. MOX works; "
    "every other control is accepted and dropped, and with no telemetry the "
    "verdict reads Unknown rather than guessing. For wiring checks only."
)
with open(path, "w") as fh:
    json.dump(manifest, fh, indent=2)
    fh.write("\n")
PY

VERSION="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["version"])' "$BUILD/plugin.json")"
ID="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["id"])' "$BUILD/plugin.json")"

mkdir -p "$OUT_DIR"
ZIP="$OUT_DIR/$ID-$VERSION-sdk14.zip"
rm -f "$ZIP"
(cd "$BUILD" && zip -qr "$ZIP" . -x "Zeus.Plugins.Contracts.dll" "*.pdb")

if command -v node >/dev/null 2>&1 && [ -d "$PLUGIN_DIR/../ubersdr/tools/panel-check" ]; then
  for panel in "$BUILD"/ui/*.es.js; do
    [ -e "$panel" ] || continue
    node "$PLUGIN_DIR/../ubersdr/tools/panel-check/check.mjs" "$panel" || {
      echo "ERROR: the panel does not render; refusing to package" >&2; exit 1; }
  done
fi

unzip -l "$ZIP" | awk '{print $4}' | grep -qx "plugin.json" \
  || { echo "ERROR: plugin.json is not at the top level of $ZIP" >&2; exit 1; }
unzip -l "$ZIP" | awk '{print $4}' | grep -qx "Zeus.Plugins.Contracts.dll" \
  && { echo "ERROR: the contracts assembly must not be shipped" >&2; exit 1; }

echo "$ZIP"
echo "  built against $SDK14_REF ($(git -C "$WORKTREE" rev-parse --short HEAD)), SDK $SDK_IN_WORKTREE"
echo "  sha256 $(shasum -a 256 "$ZIP" | cut -d' ' -f1)"
unzip -l "$ZIP" | awk 'NR>3 && NF>3 && $4!="" {print "  "$4}'
