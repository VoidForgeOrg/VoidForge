#!/bin/bash
# Per-edit hook — auto-formats C# files on Edit|Write
# Exit 0: fix applied silently. Exit 2: unfixable issue fed to Claude.
INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // .tool_input.filePath // empty')

if [[ -z "$FILE_PATH" ]] || [[ "$FILE_PATH" != *.cs ]]; then
    exit 0
fi

if [[ ! -f "$FILE_PATH" ]]; then
    exit 0
fi

SOLUTION="${CLAUDE_PROJECT_DIR:-.}/src/Voidforge.slnx"
ERRORS=""

FORMAT_OUTPUT=$(dotnet format "$SOLUTION" --include "$FILE_PATH" --verbosity quiet 2>&1)
FORMAT_EXIT=$?
if [ $FORMAT_EXIT -ne 0 ]; then
    VERIFY_OUTPUT=$(dotnet format "$SOLUTION" --include "$FILE_PATH" --verify-no-changes --verbosity quiet 2>&1)
    if [ $? -ne 0 ]; then
        ERRORS="${ERRORS}FORMAT (dotnet format):\n${VERIFY_OUTPUT}\n\n"
    fi
fi

if [ -n "$ERRORS" ]; then
    echo -e "Per-edit check found issues in ${FILE_PATH}:\n${ERRORS}" >&2
    exit 2
fi

exit 0
