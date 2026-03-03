#!/bin/bash
# Session start hook — non-blocking dependency hygiene check
cd "${CLAUDE_PROJECT_DIR:-.}"

SOLUTION="src/Voidforge.slnx"
WARNINGS=""

VULN_OUTPUT=$(dotnet list "$SOLUTION" package --vulnerable --include-transitive 2>&1)
if echo "$VULN_OUTPUT" | grep -qi "has the following vulnerable packages"; then
    WARNINGS="${WARNINGS}VULNERABLE PACKAGES:\n${VULN_OUTPUT}\n\n"
fi

OUTDATED_OUTPUT=$(dotnet list "$SOLUTION" package --outdated 2>&1)
if echo "$OUTDATED_OUTPUT" | grep -qi "has the following updates available"; then
    WARNINGS="${WARNINGS}OUTDATED PACKAGES (non-blocking):\n${OUTDATED_OUTPUT}\n\n"
fi

if [ -n "$WARNINGS" ]; then
    echo -e "Session start checks found issues:\n${WARNINGS}" >&2
    echo "These are non-blocking warnings. Consider fixing them during this session." >&2
fi

exit 0
