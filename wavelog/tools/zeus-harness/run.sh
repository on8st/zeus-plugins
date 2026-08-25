#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# End-to-end harness: a real station engine, a Zeus logbook written by something
# other than us, and this synchroniser, driven through HTTP and asserted.
#
# This exists because the unit suite cannot see what a real deployment does, and
# repeatedly it was wrong: the collection name, the JSON types a real Wavelog
# replies with, and which directory Zeus keeps its logbook in. A green suite
# proved none of them.
#
# The logbook is seeded at the PRODUCT layout —
# <data>/../ZeusProduct/logbook/zeus-logbook.db — because that is where Zeus Link
# actually keeps it. Seeding it anywhere else would test a configuration nobody
# runs.
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
mkdir -p "$SANDBOX/data" "$SANDBOX/features/be.on8st.zeus.plugins.wavelog"

cp -R "$PLUGIN_DIR/src/Zeus.Plugin.Wavelog/bin/Release/net10.0/" "$SANDBOX/features/be.on8st.zeus.plugins.wavelog/"
# The host resolves the contracts from its own load context; shipping a copy
# gives the interface types two identities and the plugin fails to bind.
rm -f "$SANDBOX/features/be.on8st.zeus.plugins.wavelog/Zeus.Plugins.Contracts.dll"
ok "plugin installed into the sandbox"

# The panel is plain ES with no build step, so nothing else would catch a syntax
# error before Zeus Link silently failed to render it.
if command -v node >/dev/null 2>&1; then
  sed "s|^import React.*|const React={createElement(){}};const useCallback=f=>f,useEffect=()=>{},useState=()=>[];|" \
    "$SANDBOX/features/be.on8st.zeus.plugins.wavelog/ui/wavelog.es.js" > "$SANDBOX/panel-check.mjs"
  if node --check "$SANDBOX/panel-check.mjs" 2>/dev/null; then ok "panel module parses"
  else bad "panel module has a syntax error"; fi
else
  echo "  --   node not installed; panel syntax unchecked"
fi

# A logbook this plugin did not create, at the layout Zeus Link really uses.
# ZeusLogbookSeed writes the document shape read out of a real product logbook.
PRODUCT_LOGBOOK="$SANDBOX/ZeusProduct/logbook/zeus-logbook.db"
"$DOTNET" run --project "$PLUGIN_DIR/tools/ZeusLogbookSeed" -c Release -- \
  "$PRODUCT_LOGBOOK" ON0HARNESS 20m USB 2026-01-01T12:00:00Z >/dev/null
[ -f "$PRODUCT_LOGBOOK" ] && ok "logbook seeded at the product layout" \
                          || bad "could not seed the logbook"

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

W="http://127.0.0.1:$PORT/api/plugins/be.on8st.zeus.plugins.wavelog"
for _ in $(seq 1 120); do curl -sf --max-time 2 -o /dev/null "$W/status" && break; sleep 0.5; done
curl -sf --max-time 5 -o /dev/null "$W/status" || { bad "engine did not come up"; tail -30 "$SANDBOX/engine.log"; exit 1; }
ok "engine up on :$PORT with the plugin"

# The assertion that would have caught the collection-name bug, and the one that
# would have caught reading the wrong directory: the plugin must see the QSOs
# that are actually in the file, and must say which file it chose.
STATUS_JSON="$(curl -sf --max-time 10 "$W/status")"
check "the synchroniser found a logbook" "$(echo "$STATUS_JSON" | json '["logbookInstalled"]')" "True"
check "it counted what is in it" "$(echo "$STATUS_JSON" | json '["qsosInLogbook"]')" "1"

ATTACHED="$(echo "$STATUS_JSON" | json '["logbookPath"]')"
case "$ATTACHED" in
  *ZeusProduct/logbook/zeus-logbook.db) ok "attached to the product logbook" ;;
  *) bad "attached to the wrong file: $ATTACHED" ;;
esac

grep -q "wavelog: the logbook has no" "$SANDBOX/engine.log" \
  && bad "attachment guard fired — the collection has been renamed" \
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
