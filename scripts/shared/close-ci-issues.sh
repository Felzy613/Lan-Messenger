#!/usr/bin/env bash
# Auto-close open CI failure issues after a successful build.
# Usage: close-ci-issues.sh <platform> <version> <sha>
# Requires: GH_TOKEN env var, gh CLI
set -euo pipefail

PLATFORM="${1:?Usage: close-ci-issues.sh <platform> <version> <sha>}"
VERSION="${2:?}"
SHA="${3:?}"

ISSUES=$(gh issue list \
    --label "ci-failure" \
    --label "platform:${PLATFORM}" \
    --state open \
    --limit 50 \
    --json number,title \
    --jq '.[].number' 2>/dev/null || true)

if [ -z "$ISSUES" ]; then
    echo "No open CI failure issues for platform:${PLATFORM} — nothing to close"
    exit 0
fi

for NUM in $ISSUES; do
    echo "Resolving issue #$NUM (${PLATFORM} v${VERSION} @ ${SHA:0:8})"
    gh issue comment "$NUM" \
        --body "✅ **Auto-resolved** — \`${PLATFORM}\` v${VERSION} built and smoke-tested successfully.

| Field | Value |
|-------|-------|
| Commit | \`${SHA}\` |
| Version | ${VERSION} |
| Date | $(date -u +%Y-%m-%dT%H:%M:%SZ) |"
    gh issue close "$NUM" --reason completed
done

echo "Closed $( echo "$ISSUES" | wc -w | tr -d ' ') issue(s) for platform:${PLATFORM}"
