#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# End-to-end harness: a real station engine, the real Zeus Logbook plugin, and
# this synchroniser, driven through HTTP and asserted.
#
# This exists because the unit suite cannot see the two things most likely to be
# wrong, and both were: the name of the collection the reference writes, and the
# JSON types a real Wavelog replies with. A green suite proved neither.
#
# Nothing here touches the installed Zeus. ZEUS_PREFS_PATH and ZEUS_PLUGINS_PATH
# move the whole data directory and plugin root into a sandbox — the engine's own
# comment names dev, CI and tests as the reason those exist.
#
# Usage:
#   ./run.sh                 # against the local fake Wavelog (default, offline)
#   ./run.sh --live          # against a real Wavelog; needs WAVELOG_URL + WAVELOG_KEY
#                            # READ-ONLY unless --allow-write is also given
#   ./run.sh --live --allow-write --station-profile N
#
set -euo pipefail

# ---- the reference logbook plugin, pinned ----------------------------------
# Checked, not trusted: the registry serves the hash and we verify it. This is
# also the artefact that defines what "the operator's logbook" means, so its
# version is part of what the harness pins.
LOGBOOK_VERSION="1.1.0"
LOGBOOK_URL="https://downloads.zeussdr.com/plugins/releases/download/logbook-v${LOGBOOK_VERSION}/logbook-${LOGBOOK_VERSION}.zip"
LOGBOOK_SHA256="1a7bd5399dd723ad94658a8a1eb6e44d4a2f0e5a4c863783c35b4182362d1dff"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$(cd "$HERE/../.." && pwd)"
ENGINE_REPO="${ENGINE_REPO:-$(cd "$PLUGIN_DIR/../.." && pwd)/station-engine}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
PORT="${PORT:-6191}"
FAKE_PORT="${FAKE_PORT:-8099}"

LIVE=0; ALLOW_WRITE=0; STATION_PROFILE="${STATION_PROFILE:-1}"
while [ $# -gt 0 ]; do
  case "$1" in
    --live) LIVE=1 ;;
    --allow-write) ALLOW_WRITE=1 ;;
    --station-profile) STATION_PROFILE="$2"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/zeus-harness.XXXXXX")"
ENGINE_PID=""; FAKE_PID=""
cleanup() {
  [ -n "$ENGINE_PID" ] && kill "$ENGINE_PID" 2>/dev/null || true
  [ -n "$FAKE_PID" ]   && kill "$FAKE_PID" 2>/dev/null || true
  echo "sandbox left at $SANDBOX"
}
trap cleanup EXIT

FAILURES=0
ok()   { printf '  \033[32mok\033[0m   %s\n' "$1"; }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$1"; FAILURES=$((FAILURES+1)); }
check() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 — expected [$3], got [$2]"; fi; }
section() { printf '\n\033[1m%s\033[0m\n' "$1"; }

json() { python3 -c 'import json,sys;d=json.load(sys.stdin);print(eval("d"+sys.argv[1]))' "$1"; }

# ---- build ------------------------------------------------------------------
section "build"
"$DOTNET" build "$PLUGIN_DIR/src/Zeus.Plugin.Wavelog/Zeus.Plugin.Wavelog.csproj" -c Release >/dev/null
"$DOTNET" build "$ENGINE_REPO/StationEngine/StationEngine.csproj" -c Release >/dev/null
ok "plugin and engine built"

# ---- install ----------------------------------------------------------------
section "install"
mkdir -p "$SANDBOX/data" "$SANDBOX/features/on8st.wavelog" "$SANDBOX/features/org.openhpsdr.logbook"

curl -sL --max-time 120 -o "$SANDBOX/logbook.zip" "$LOGBOOK_URL"
ACTUAL="$(shasum -a 256 "$SANDBOX/logbook.zip" | cut -d' ' -f1)"
check "reference logbook v$LOGBOOK_VERSION checksum" "$ACTUAL" "$LOGBOOK_SHA256"
[ "$ACTUAL" = "$LOGBOOK_SHA256" ] || exit 1
unzip -oq "$SANDBOX/logbook.zip" -d "$SANDBOX/features/org.openhpsdr.logbook"

cp -R "$PLUGIN_DIR/src/Zeus.Plugin.Wavelog/bin/Release/net10.0/" "$SANDBOX/features/on8st.wavelog/"
# The host resolves the contracts from its own load context; shipping a copy
# gives the interface types two identities and the plugin fails to bind.
rm -f "$SANDBOX/features/on8st.wavelog/Zeus.Plugins.Contracts.dll"
ok "both plugins installed into the sandbox"

# The panel is plain ES with no build step, so nothing else would catch a syntax
# error before Zeus Link silently failed to render it.
if command -v node >/dev/null 2>&1; then
  sed "s|^import React.*|const React={createElement(){}};const useCallback=f=>f,useEffect=()=>{},useState=()=>[];|" \
    "$SANDBOX/features/on8st.wavelog/ui/wavelog.es.js" > "$SANDBOX/panel-check.mjs"
  if node --check "$SANDBOX/panel-check.mjs" 2>/dev/null; then ok "panel module parses"
  else bad "panel module has a syntax error"; fi
else
  echo "  --   node not installed; panel syntax unchecked"
fi

# ---- wavelog ----------------------------------------------------------------
if [ "$LIVE" = "1" ]; then
  : "${WAVELOG_URL:?--live needs WAVELOG_URL}"
  : "${WAVELOG_KEY:?--live needs WAVELOG_KEY}"
  WL_URL="$WAVELOG_URL"; WL_KEY="$WAVELOG_KEY"
  section "live Wavelog: $WL_URL (profile $STATION_PROFILE, write=$ALLOW_WRITE)"
else
  "$DOTNET" run --project "$PLUGIN_DIR/tools/FakeWavelog" -c Release -- "$FAKE_PORT" \
    > "$SANDBOX/fake.log" 2>&1 &
  FAKE_PID=$!
  for _ in $(seq 1 60); do grep -q "fake wavelog on" "$SANDBOX/fake.log" && break; sleep 0.5; done
  WL_URL="http://127.0.0.1:$FAKE_PORT"; WL_KEY="test-key"
  section "fake Wavelog on $WL_URL"
fi

# ---- engine -----------------------------------------------------------------
section "engine"
DOTNET_ROOT="$(dirname "$DOTNET")" \
ZEUS_PREFS_PATH="$SANDBOX/data/zeus-prefs.db" \
ZEUS_PLUGINS_PATH="$SANDBOX/features" \
"$DOTNET" "$ENGINE_REPO/StationEngine/bin/Release/net10.0/StationEngine.dll" \
  --port "$PORT" --bind loopback > "$SANDBOX/engine.log" 2>&1 &
ENGINE_PID=$!

W="http://127.0.0.1:$PORT/api/plugins/on8st.wavelog"
L="http://127.0.0.1:$PORT/api/plugins/org.openhpsdr.logbook"
for _ in $(seq 1 120); do curl -sf --max-time 2 -o /dev/null "$W/status" && break; sleep 0.5; done
curl -sf --max-time 5 -o /dev/null "$W/status" || { bad "engine did not come up"; tail -30 "$SANDBOX/engine.log"; exit 1; }
ok "engine up on :$PORT with both plugins"

grep -q "Loaded plugin org.openhpsdr.logbook" "$SANDBOX/engine.log" \
  && ok "reference logbook loaded" || bad "reference logbook did not load"

# The assertion that would have caught the collection-name bug: the plugin must
# report the same number of QSOs the logbook itself reports. Equal-and-zero is
# not proof, so a QSO is logged first.
curl -sf --max-time 10 -X POST "$L/entry" -H 'content-type: application/json' -d '{
  "callsign":"ON0HARNESS","frequencyMhz":14.074,"band":"20m","mode":"USB",
  "rstSent":"59","rstRcvd":"57","qsoDateTimeUtc":"2026-01-01T12:00:00Z"}' >/dev/null

LB_COUNT="$(curl -sf --max-time 10 "$L/entries?skip=0&take=1" | json '["totalCount"]')"
WL_COUNT="$(curl -sf --max-time 10 "$W/status" | json '["qsosInLogbook"]')"
check "the synchroniser sees the logbook the reference writes" "$WL_COUNT" "$LB_COUNT"
grep -q "wavelog: the logbook has no" "$SANDBOX/engine.log" \
  && bad "attachment guard fired — the reference has renamed its collection" \
  || ok "attachment guard clean"

# ---- configure --------------------------------------------------------------
section "configure"
PUSH=true
[ "$LIVE" = "1" ] && [ "$ALLOW_WRITE" = "0" ] && PUSH=false

curl -sf --max-time 15 -X POST "$W/config" -H 'content-type: application/json' -d "{
  \"baseUrl\":\"$WL_URL\",\"apiKey\":\"$WL_KEY\",
  \"stationProfileId\":$STATION_PROFILE,\"pullStationIds\":[$STATION_PROFILE],
  \"pushEnabled\":$PUSH,\"pullEnabled\":true,\"radioEnabled\":false}" >/dev/null
ok "configured (push=$PUSH)"

KEY_LEAK="$(curl -sf --max-time 10 "$W/config" | grep -c "$WL_KEY" || true)"
check "the config endpoint never returns the key" "$KEY_LEAK" "0"

TEST="$(curl -sf --max-time 30 -X POST "$W/test" | json '["ok"]')"
check "reaches Wavelog" "$TEST" "True"

# ---- sync -------------------------------------------------------------------
section "sync"
# Wait for one full scan+pull cycle rather than sleeping a guessed interval.
for _ in $(seq 1 60); do grep -q "wavelog: pulled" "$SANDBOX/engine.log" && break; sleep 2; done

if grep -q "sync loop failed" "$SANDBOX/engine.log"; then
  bad "the sync loop threw"
  grep -A 6 "sync loop failed" "$SANDBOX/engine.log" | head -8
else
  ok "sync loop ran clean"
fi

STATUS="$(curl -sf --max-time 10 "$W/status")"
check "nothing dead-lettered" "$(echo "$STATUS" | json '["failed"]')" "0"

if [ "$PUSH" = "true" ]; then
  for _ in $(seq 1 45); do
    [ "$(curl -sf --max-time 5 "$W/status" | json '["pending"]')" = "0" ] && break; sleep 2
  done
  check "the queue drained" "$(curl -sf --max-time 10 "$W/status" | json '["pending"]')" "0"
fi

# The trap: what came back on the pull must not be queued for push-back.
RESYNC="$(curl -sf --max-time 60 -X POST "$W/resync" -H 'content-type: application/json' -d '{"dryRun":true}')"
echo "  resync dry run: $RESYNC"
if [ "$PUSH" = "true" ]; then
  check "no drift toward Wavelog" "$(echo "$RESYNC" | json '["missingThere"]')" "0"
  check "no drift toward Zeus"    "$(echo "$RESYNC" | json '["missingHere"]')" "0"
fi

# ---- verdict ----------------------------------------------------------------
section "result"
if [ "$FAILURES" = "0" ]; then
  printf '\033[32mall checks passed\033[0m\n'
else
  printf '\033[31m%s check(s) failed\033[0m\n' "$FAILURES"
fi
exit "$FAILURES"
