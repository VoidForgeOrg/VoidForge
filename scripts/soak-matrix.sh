#!/usr/bin/env bash
# Run every soak scenario IN PARALLEL, one OS process each.
#
# Why one process per scenario (not xUnit parallelism): the API host installs a process-global economy
# rate table (BuildingSpecs.Current, "fixed for the process lifetime; differing rates require a separate
# process") and binds config via process-global environment variables set just before boot. Two scenario
# hosts in one process would share both. Separate processes each get their own statics, and each scenario
# already targets its OWN database on the shared Postgres server (SoakScenario.DbName), so the parallel
# runs never collide.
#
# Usage:
#   bash scripts/soak-matrix.sh                 # default 120s window
#   SOAK_WINDOW_SECONDS=300 bash scripts/soak-matrix.sh
#
# Requires Postgres up (bash .claude/start-infra.sh). Exit code is non-zero if ANY scenario fails.
set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

PROJECT="src/Voidforge.SoakTests/Voidforge.SoakTests.csproj"

# One entry per scenario: "<label>=<test-name filter>". Add a line here when a scenario lands.
SCENARIOS=(
  "two-user-economy=TwoUserEconomy"
  "input-starvation=InputStarvation"
)

LOG_DIR="$(mktemp -d)"
echo "Soak matrix: ${#SCENARIOS[@]} scenario(s), window ${SOAK_WINDOW_SECONDS:-120}s, logs in $LOG_DIR"

# Build ONCE up front so the parallel runs don't race on obj/bin, then run each with --no-build.
echo "Building $PROJECT ..."
if ! dotnet build "$PROJECT" --verbosity quiet; then
  echo "BUILD FAILED — aborting." >&2
  exit 1
fi

declare -a PIDS=()
declare -a LABELS=()
for entry in "${SCENARIOS[@]}"; do
  label="${entry%%=*}"
  filter="${entry#*=}"
  log="$LOG_DIR/$label.log"
  echo "  -> launching $label (filter FullyQualifiedName~$filter)"
  dotnet test "$PROJECT" --no-build \
    --filter "FullyQualifiedName~$filter" \
    -l "console;verbosity=detailed" >"$log" 2>&1 &
  PIDS+=("$!")
  LABELS+=("$label")
done

# Wait for each and collect its exit code.
FAILED=0
echo
echo "=== Results ==="
for i in "${!PIDS[@]}"; do
  if wait "${PIDS[$i]}"; then
    printf "  PASS  %s\n" "${LABELS[$i]}"
  else
    printf "  FAIL  %s   (see %s/%s.log)\n" "${LABELS[$i]}" "$LOG_DIR" "${LABELS[$i]}"
    FAILED=1
  fi
done

echo
if [[ "$FAILED" -ne 0 ]]; then
  echo "Soak matrix: at least one scenario FAILED. Logs: $LOG_DIR"
  exit 1
fi
echo "Soak matrix: all scenarios PASSED. Logs: $LOG_DIR"
