#!/usr/bin/env bash
# Delta Encoding v2 — integration test harness
# Automates: build, deploy, server start, log checks, netem loss, cleanup
# Manual: client GUI, gameplay observation, pass/fail judgment
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
VS_DIR="${VINTAGE_STORY:-/home/vitorpn/Misc/vintagestory}"
# Separate data paths — server uses default, client uses same default (needs account/settings)
SERVER_DATA="$HOME/.config/VintagestoryData"
CLIENT_DATA="$HOME/.config/VintagestoryData"
# Separate log paths to avoid server/client log collision
SERVER_LOGS="$PROJECT_DIR/tasks/test-logs/server"
CLIENT_LOGS="$SERVER_DATA/Logs"
SERVER_LOG="$SERVER_LOGS/server-main.log"
CLIENT_LOG="$CLIENT_LOGS/client-main.log"
PORT=42420
SERVER_PID=""
NETEM_ACTIVE=0
RESULTS_FILE="$PROJECT_DIR/tasks/test-results-delta-v2.md"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

cleanup() {
    echo ""
    echo -e "${YELLOW}Cleaning up...${NC}"
    if [[ $NETEM_ACTIVE -eq 1 ]]; then
        sudo tc qdisc del dev lo root 2>/dev/null && echo "  netem removed" || true
        NETEM_ACTIVE=0
    fi
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null
        wait "$SERVER_PID" 2>/dev/null || true
        echo "  server stopped (PID $SERVER_PID)"
    fi
}
trap cleanup EXIT

die() { echo -e "${RED}FATAL: $1${NC}" >&2; exit 1; }
info() { echo -e "${CYAN}▶ $1${NC}"; }
ok() { echo -e "${GREEN}✓ $1${NC}"; }
warn() { echo -e "${YELLOW}⚠ $1${NC}"; }

pause() {
    echo ""
    echo -e "${YELLOW}$1${NC}"
    echo -n "Press Enter to continue..."
    read -r
}

ask_pass_fail() {
    local tc_name="$1"
    while true; do
        echo -n -e "  ${CYAN}$tc_name${NC} — Pass/Fail/Skip? [p/f/s]: "
        read -r answer
        case "${answer,,}" in
            p) echo "PASS"; return 0 ;;
            f) echo "FAIL"; return 1 ;;
            s) echo "SKIP"; return 0 ;;
            *) echo "  (enter p, f, or s)" ;;
        esac
    done
}

# ─── PREFLIGHT ────────────────────────────────────────────────────────────────

info "Preflight checks"

[[ -f "$VS_DIR/VintagestoryServer.dll" ]] || die "VS server not found at $VS_DIR"
[[ -f "$VS_DIR/VintagestoryAPI.dll" ]] || die "VS API not found at $VS_DIR"
command -v dotnet >/dev/null || die "dotnet not found"
dotnet --list-runtimes | grep -q "NETCore.App 10\." || die ".NET 10 runtime not found"
ok "VS server at $VS_DIR, .NET 10 present"

# ─── BUILD ────────────────────────────────────────────────────────────────────

info "Building Synergy..."
cd "$PROJECT_DIR"
export VINTAGE_STORY="$VS_DIR"
./build.sh -nodeploy
MOD_ZIP="$(ls bin/Synergy-*.zip 2>/dev/null | head -1)"
[[ -f "$MOD_ZIP" ]] || die "Build produced no zip"
ok "Built: $MOD_ZIP"

# ─── DEPLOY ───────────────────────────────────────────────────────────────────

info "Deploying mod + setting up isolated paths"

# Server: uses its normal data path but separate log dir
mkdir -p "$SERVER_DATA/Mods" "$SERVER_LOGS"

rm -f "$SERVER_DATA/Mods"/Synergy*.zip
cp "$MOD_ZIP" "$SERVER_DATA/Mods/"

ok "Deployed to $SERVER_DATA/Mods/"

# Ensure DeltaEncodingEnabled is true in server config
if [[ -f "$SERVER_DATA/ModConfig/Synergy.json" ]]; then
    if ! grep -q '"DeltaEncodingEnabled": true' "$SERVER_DATA/ModConfig/Synergy.json"; then
        warn "DeltaEncodingEnabled was not true — fixing"
        sed -i 's/"DeltaEncodingEnabled": false/"DeltaEncodingEnabled": true/' "$SERVER_DATA/ModConfig/Synergy.json"
    fi
fi

# ─── CLEAR LOGS ───────────────────────────────────────────────────────────────

info "Clearing test logs"
rm -f "$SERVER_LOGS"/*.log
rm -f "$CLIENT_LOGS"/client-main.log "$CLIENT_LOGS"/client-debug.log
ok "Logs cleared"

# ─── START SERVER ─────────────────────────────────────────────────────────────

info "Starting dedicated server (port $PORT)..."

CONSOLE_LOG="$SERVER_LOGS/server-console.log"
cd "$VS_DIR"
dotnet VintagestoryServer.dll \
    --dataPath "$SERVER_DATA" \
    --logPath "$SERVER_LOGS" \
    &>"$CONSOLE_LOG" &
SERVER_PID=$!
echo "  PID: $SERVER_PID"
echo "  dataPath: $SERVER_DATA"
echo "  logPath:  $SERVER_LOGS"

# Wait for server ready (server-main.log gets "GameReady" when fully loaded)
TIMEOUT=90
ELAPSED=0
while ! grep -q "runphase GameReady" "$SERVER_LOG" 2>/dev/null; do
    sleep 2
    ELAPSED=$((ELAPSED + 2))
    if [[ $ELAPSED -ge $TIMEOUT ]]; then
        echo "  Last lines of server-main.log:"
        tail -10 "$SERVER_LOG" 2>/dev/null
        die "Server did not reach GameReady within ${TIMEOUT}s"
    fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "  Last lines of console:"
        tail -20 "$CONSOLE_LOG" 2>/dev/null
        die "Server process died"
    fi
done
ok "Server running (${ELAPSED}s)"

# ─── WAIT FOR CLIENT ──────────────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${YELLOW}  MANUAL STEP: Launch the client GUI${NC}"
echo ""
echo "  Command (copy-paste into another terminal):"
echo ""
echo "    cd $VS_DIR && ./Vintagestory"
echo ""
echo "  1. Launch in another terminal"
echo "  2. Multiplayer → Connect to: localhost"
echo "  3. Run:  /gamemode creative"
echo "  4. Come back here when connected"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
pause "Waiting for you to connect the client..."

# ─── VERIFY PROTOCOL ACTIVE ──────────────────────────────────────────────────

info "Checking protocol activation in logs..."

PROTO_OK=1
if grep -q "DeltaEncoding: Active\|DeltaEncoding:.*enabled" "$SERVER_LOG" 2>/dev/null; then
    ok "Server: DeltaEncoding active"
else
    warn "Server: DeltaEncoding activation not found in log"
    PROTO_OK=0
fi

if grep -q "delta-ack=True\|delta-ack=true" "$SERVER_LOG" 2>/dev/null; then
    ok "Server: Client handshake delta-ack=True"
else
    warn "Server: Handshake delta-ack not found (client may not have Synergy?)"
    PROTO_OK=0
fi

if grep -q "Server handshake received\|delta-ack" "$CLIENT_LOG" 2>/dev/null; then
    ok "Client: Handshake confirmed"
else
    warn "Client: Handshake not confirmed in log"
    PROTO_OK=0
fi

if [[ $PROTO_OK -eq 0 ]]; then
    echo ""
    warn "Protocol may not be fully active. Check both sides have the same Synergy version."
    echo "  Server log: $SERVER_LOG"
    echo "  Client log: $CLIENT_LOG"
    pause "Continue anyway? (Ctrl+C to abort)"
fi

# ─── DECLARE RESULTS ──────────────────────────────────────────────────────────

declare -A RESULTS

# ─── TC1: NO LOSS SANITY ──────────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC1: Drop 1 item (no loss) — sanity check${NC}"
echo ""
echo "  → Press Q to drop an item from your hand"
echo "  → Expected: Item lands on the ground within ~1s, stops spinning"
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC1]=$(ask_pass_fail "TC1: single drop, no loss")

# ─── APPLY 15% LOSS ──────────────────────────────────────────────────────────

info "Applying 15% packet loss on loopback..."
sudo tc qdisc add dev lo root netem loss 15%
NETEM_ACTIVE=1
ok "netem active: 15% loss on lo"

# ─── TC2: BURST DROP @15% ─────────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC2: Drop 10+ items rapidly (15% loss)${NC}"
echo ""
echo "  → Hold Q or drop items quickly from creative inventory"
echo "  → Expected: ALL items settle on ground and STOP spinning. NONE stuck at hand."
echo "  → Wait ~5s after dropping."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC2]=$(ask_pass_fail "TC2: burst drop @15%")

# ─── TC4: WALKING MOB @15% ────────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC4: Spawn + watch a mob walk (15% loss)${NC}"
echo ""
echo "  → Type in chat: /entity spawn game:chicken-hen 3"
echo "  → Expected: Smooth movement, brief hitches OK, no permanent freeze."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC4]=$(ask_pass_fail "TC4: walking mob @15%")

# ─── RAMP TO 25% BURSTY ──────────────────────────────────────────────────────

info "Ramping to 25% bursty loss (25% correlation)..."
sudo tc qdisc change dev lo root netem loss 25% 25%
ok "netem: 25% bursty loss"

# ─── TC5: THE CRITICAL TEST ──────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC5: Drop 20 items under 25% bursty loss — THE v1 BUG CASE${NC}"
echo ""
echo "  → Drop 20 items, wait 10s"
echo "  → Expected: EVERY item ends on the ground and stops spinning."
echo "  → NONE stuck at hand level. This is the scenario that broke v1."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC5]=$(ask_pass_fail "TC5: 20 items @25% bursty (THE critical test)")

# ─── TC6: REMOVE LOSS → CONVERGENCE ──────────────────────────────────────────

info "Removing packet loss..."
sudo tc qdisc del dev lo root
NETEM_ACTIVE=0
ok "netem removed"

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC6: Consistency after loss removed${NC}"
echo ""
echo "  → Walk over dropped items"
echo "  → Expected: Within ~2s everything consistent. All pickupable at correct spots."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC6]=$(ask_pass_fail "TC6: convergence after loss removed")

# ─── TC7: DISTANCE RE-TRACK ──────────────────────────────────────────────────

info "Applying 15% loss for distance test..."
sudo tc qdisc add dev lo root netem loss 15%
NETEM_ACTIVE=1

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC7: Teleport far + back (15% loss)${NC}"
echo ""
echo "  → Type in chat: /tp ~250 ~ ~"
echo "  → Wait 3s, then: /tp ~-250 ~ ~"
echo "  → Expected: Entities reappear at correct positions."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC7]=$(ask_pass_fail "TC7: distance re-track @15%")

# ─── TC8: RECONNECT ──────────────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC8: Reconnect under loss${NC}"
echo ""
echo "  → Esc → Disconnect → Reconnect to localhost"
echo "  → Expected: All entities + dropped items visible after reconnect."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC8]=$(ask_pass_fail "TC8: reconnect under loss")

# ─── REMOVE LOSS ──────────────────────────────────────────────────────────────

sudo tc qdisc del dev lo root
NETEM_ACTIVE=0

# ─── TC10: STATIONARY CONTROL ─────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${CYAN}  TC10: Stationary entities (no loss)${NC}"
echo ""
echo "  → Place a chest; spawn a chicken, let it stop moving"
echo "  → Expected: Always visible, no regression."
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
RESULTS[TC10]=$(ask_pass_fail "TC10: stationary control")

# ─── POST-FLIGHT LOG CHECK ────────────────────────────────────────────────────

echo ""
info "Post-flight log analysis..."

ERRORS=0

echo "  Server log:"
AD_COUNT=$(grep -c "Auto-disabled" "$SERVER_LOG" 2>/dev/null || echo "0")
if [[ "$AD_COUNT" -gt 0 ]]; then
    echo -e "    ${RED}✗ Auto-disabled found ($AD_COUNT occurrences)${NC}"
    grep "Auto-disabled" "$SERVER_LOG" | tail -5
    ERRORS=$((ERRORS + 1))
else
    echo -e "    ${GREEN}✓ No Auto-disabled events${NC}"
fi

ERR_COUNT=$(grep -cE "DeltaEncoding.*[Ee]rror|DeltaEncoding.*[Ee]xception" "$SERVER_LOG" 2>/dev/null || echo "0")
if [[ "$ERR_COUNT" -gt 0 ]]; then
    echo -e "    ${RED}✗ DeltaEncoding errors found ($ERR_COUNT)${NC}"
    grep -E "DeltaEncoding.*[Ee]rror|DeltaEncoding.*[Ee]xception" "$SERVER_LOG" | tail -5
    ERRORS=$((ERRORS + 1))
else
    echo -e "    ${GREEN}✓ No DeltaEncoding errors${NC}"
fi

echo "  Client log:"
CLI_AD=$(grep -c "Auto-disabled" "$CLIENT_LOG" 2>/dev/null || echo "0")
if [[ "$CLI_AD" -gt 0 ]]; then
    echo -e "    ${RED}✗ Auto-disabled found ($CLI_AD)${NC}"
    ERRORS=$((ERRORS + 1))
else
    echo -e "    ${GREEN}✓ No Auto-disabled events${NC}"
fi

CLI_ERR=$(grep -cE "Delta decode error|DeltaPosition.*[Ee]rror" "$CLIENT_LOG" 2>/dev/null || echo "0")
if [[ "$CLI_ERR" -gt 0 ]]; then
    echo -e "    ${RED}✗ Delta decode errors ($CLI_ERR)${NC}"
    grep -E "Delta decode error|DeltaPosition.*[Ee]rror" "$CLIENT_LOG" | tail -5
    ERRORS=$((ERRORS + 1))
else
    echo -e "    ${GREEN}✓ No decode errors${NC}"
fi

RESULTS[LOGS]=$( [[ $ERRORS -eq 0 ]] && echo "PASS" || echo "FAIL" )

# ─── GENERATE REPORT ─────────────────────────────────────────────────────────

info "Generating test report..."

mkdir -p "$(dirname "$RESULTS_FILE")"
cat > "$RESULTS_FILE" << EOF
# Delta Encoding v2 — Integration Test Results

**Date:** $(date '+%Y-%m-%d %H:%M:%S %Z')
**Mod version:** $(grep -oP '"version"\s*:\s*"\K[^"]+' "$PROJECT_DIR/modinfo.json")
**VS version:** $(grep -oP 'Version [0-9.]+' "$CONSOLE_LOG" 2>/dev/null | head -1 || echo "unknown")
**Environment:** loopback (single machine), tc/netem

## Results

| Test | Result | Notes |
|------|--------|-------|
| Protocol active (delta-ack=True) | $( [[ $PROTO_OK -eq 1 ]] && echo "PASS" || echo "WARN" ) | |
| TC1: single drop (no loss) | ${RESULTS[TC1]} | |
| TC2: burst drop @15% | ${RESULTS[TC2]} | |
| TC4: walking mob @15% | ${RESULTS[TC4]} | |
| TC5: 20 items @25% bursty (**critical**) | ${RESULTS[TC5]} | |
| TC6: convergence after loss removed | ${RESULTS[TC6]} | |
| TC7: distance re-track @15% | ${RESULTS[TC7]} | |
| TC8: reconnect under loss | ${RESULTS[TC8]} | |
| TC10: stationary control | ${RESULTS[TC10]} | |
| Logs clean (no Auto-disabled/errors) | ${RESULTS[LOGS]} | |

## Verdict

EOF

# Count failures
FAIL_COUNT=0
for key in "${!RESULTS[@]}"; do
    [[ "${RESULTS[$key]}" == "FAIL" ]] && FAIL_COUNT=$((FAIL_COUNT + 1))
done

if [[ $FAIL_COUNT -eq 0 ]]; then
    echo "**✅ ALL PASSED — ready for release.**" >> "$RESULTS_FILE"
    echo ""
    echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${GREEN}  ALL TESTS PASSED — Delta Encoding v2 ready for release${NC}"
    echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
else
    echo "**❌ $FAIL_COUNT FAILURE(S) — DO NOT RELEASE.**" >> "$RESULTS_FILE"
    echo ""
    echo -e "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${RED}  $FAIL_COUNT TEST(S) FAILED — investigate before release${NC}"
    echo -e "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
fi

echo ""
echo "Report saved: $RESULTS_FILE"
