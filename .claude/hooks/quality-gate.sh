#!/bin/bash
# Quality gate hook — Claude Code Stop event
# Fail-fast: exits on first failure (exit 2) so Claude fixes one issue at a time.
set -o pipefail

cd "${CLAUDE_PROJECT_DIR:-.}"

HOOK_LOG="${CLAUDE_PROJECT_DIR:-.}/.claude/hooks/hook-debug.log"
WORKTREE_ID="$(basename "${CLAUDE_PROJECT_DIR:-.}")"
debuglog() {
    echo "[quality-gate@${WORKTREE_ID}] $(date '+%Y-%m-%d %H:%M:%S') $1" >> "$HOOK_LOG"
}
debuglog "=== HOOK STARTED (pid=$$) ==="

SOLUTION="src/Voidforge.slnx"

declare -A TOOL_HINTS
TOOL_HINTS=(
    [dotnet-restore]="Check src/Voidforge.Api/Voidforge.Api.csproj and src/Directory.Build.props for malformed package references or missing NuGet sources. Run 'dotnet restore src/Voidforge.slnx' manually to see the full error."
    [dotnet-build]="Read the file at the reported line and column number. Fix the compiler error or analyzer warning. Run 'dotnet build src/Voidforge.slnx --no-restore' to re-check a single project after fixing."
    [dotnet-format]="Run 'dotnet format src/Voidforge.slnx --include <file>' to auto-fix formatting. Check src/.editorconfig rules if the fix doesn't apply."
    [security-audit]="Run 'dotnet list src/Voidforge.slnx package --vulnerable --include-transitive' to see all vulnerable packages. Update the affected package version in the .csproj file."
)

fail() {
    local name="$1"
    local cmd="$2"
    local output="$3"
    local hint="${TOOL_HINTS[$name]:-}"

    echo "" >&2
    echo "QUALITY GATE FAILED [$name]:" >&2
    echo "Command: $cmd" >&2
    echo "" >&2
    echo "$output" >&2
    echo "" >&2
    if [ -n "$hint" ]; then
        echo "Hint: $hint" >&2
        echo "" >&2
    fi
    echo "ACTION REQUIRED: You MUST fix the issue shown above. Do NOT stop or explain — read the failing file, edit the source code to resolve it, and the quality gate will re-run automatically." >&2
    debuglog "=== FAILED: $name ==="
    exit 2
}

run_check() {
    local name="$1"; shift
    local cmd="$*"
    debuglog "Running $name..."
    OUTPUT=$("$@" 2>&1) || fail "$name" "$cmd" "$OUTPUT"
}

run_check_nonempty() {
    local name="$1"; shift
    local cmd="$*"
    debuglog "Running $name..."
    OUTPUT=$("$@" 2>&1)
    [ -n "$OUTPUT" ] && fail "$name" "$cmd" "$OUTPUT"
}

# [check:dotnet-restore]
run_check "dotnet-restore" dotnet restore "$SOLUTION" --verbosity quiet

# [check:dotnet-build]
run_check "dotnet-build" dotnet build "$SOLUTION" --no-restore --verbosity quiet -warnaserror

# [check:dotnet-format]
run_check "dotnet-format" dotnet format "$SOLUTION" --verify-no-changes --verbosity quiet

# [check:security-audit]
VULN_OUTPUT=$(dotnet list "$SOLUTION" package --vulnerable --include-transitive 2>&1)
if echo "$VULN_OUTPUT" | grep -qi "has the following vulnerable packages"; then
    fail "security-audit" "dotnet list $SOLUTION package --vulnerable --include-transitive" "$VULN_OUTPUT"
fi

# [check:dotnet-test] — uncomment when test projects are added
# run_check "dotnet-test" dotnet test "$SOLUTION" --no-build --verbosity quiet

debuglog "=== ALL CHECKS PASSED ==="
exit 0
