#!/usr/bin/env bash
# Generate user-friendly, categorized release notes from git commits.
#
# Usage:
#   generate-release-notes.sh <platform> <version> [prev-tag]
#
#   platform  — "macos", "windows", or "combined"
#   version   — current version string (e.g. "1.6.6")
#   prev-tag  — (optional) git tag or SHA to diff from; auto-detected if omitted
#
# Output: markdown to stdout.
# Requires: git, gh (GitHub CLI), GH_TOKEN env var when calling the API.
set -euo pipefail

PLATFORM="${1:-}"
VERSION="${2:-}"
PREV_ARG="${3:-}"

if [ -z "$PLATFORM" ] || [ -z "$VERSION" ]; then
  echo "Usage: $0 <platform> <version> [prev-tag]" >&2
  exit 1
fi

# ── Determine the starting point for the commit range ──────────────────────
if [ -n "$PREV_ARG" ]; then
  PREV_SHA="$PREV_ARG"
else
  # Auto-detect from the most recent release for this platform.
  case "$PLATFORM" in
    combined) TAG_PREFIX="release-" ;;
    *)        TAG_PREFIX="${PLATFORM}-v" ;;
  esac

  PREV_TAG=$(gh release list --limit 100 \
    --json tagName,createdAt \
    --jq "[.[] | select(.tagName | startswith(\"${TAG_PREFIX}\"))] | .[0].tagName // empty" \
    2>/dev/null || true)

  if [ -n "$PREV_TAG" ]; then
    PREV_SHA=$(gh release view "$PREV_TAG" \
      --json targetCommitish -q .targetCommitish 2>/dev/null || true)
    echo "Generating notes since: $PREV_TAG (${PREV_SHA:0:8})" >&2
  fi
fi

# ── Collect commits ─────────────────────────────────────────────────────────
if [ -n "${PREV_SHA:-}" ]; then
  COMMITS=$(git log --no-merges --pretty="format:%s" "${PREV_SHA}..HEAD" 2>/dev/null \
    | head -80 || true)
else
  COMMITS=$(git log --no-merges --pretty="format:%s" -25 HEAD 2>/dev/null || true)
fi

# ── Categorize commits ───────────────────────────────────────────────────────
FEATURES=""
IMPROVEMENTS=""
FIXES=""
PERFORMANCE=""
INTERNAL=""

while IFS= read -r msg; do
  [ -z "$msg" ] && continue

  # Skip obviously noisy / non-user-facing commits
  lower=$(printf '%s' "$msg" | tr '[:upper:]' '[:lower:]')
  case "$lower" in
    merge\ *|"bump version"*|"chore(deps)"*|"chore: bump"*|\
    "chore(version)"*|"update version"*|"sync version"*|\
    formatting\ *|"no functional"*|"wip:"*|"temp:"*|"tmp:"*)
      continue ;;
  esac
  [[ "$lower" =~ ^(revert\ )?merge\ (pull\ request|branch) ]] && continue

  # Strip conventional-commit prefix (case-insensitive)
  DISPLAY=$(printf '%s' "$msg" \
    | sed -E 's/^(feat|fix|perf|chore|docs|style|refactor|test|ci|build)(\([^)]+\))?!?:[[:space:]]*//' \
    | sed -E 's/^(macOS|Windows|macos|windows):[[:space:]]*//')

  # Capitalize first character
  DISPLAY="$(printf '%s' "${DISPLAY:0:1}" | tr '[:lower:]' '[:upper:]')${DISPLAY:1}"

  # Assign to category
  if   [[ "$msg" =~ ^feat ]]; then        FEATURES="${FEATURES}- ${DISPLAY}\n"
  elif [[ "$msg" =~ ^fix ]]; then         FIXES="${FIXES}- ${DISPLAY}\n"
  elif [[ "$msg" =~ ^perf ]]; then        PERFORMANCE="${PERFORMANCE}- ${DISPLAY}\n"
  elif [[ "$msg" =~ ^(chore|docs|style|refactor|test|ci|build) ]]; then
    INTERNAL="${INTERNAL}- ${DISPLAY}\n"
  else
    IMPROVEMENTS="${IMPROVEMENTS}- ${DISPLAY}\n"
  fi

done <<< "$COMMITS"

# ── Emit markdown ────────────────────────────────────────────────────────────
HAS_CONTENT=false

if [ -n "$FEATURES" ]; then
  printf '### ✨ New Features\n\n%b\n' "$FEATURES"
  HAS_CONTENT=true
fi

if [ -n "$IMPROVEMENTS" ]; then
  printf '### 🔧 Improvements\n\n%b\n' "$IMPROVEMENTS"
  HAS_CONTENT=true
fi

if [ -n "$FIXES" ]; then
  printf '### 🐛 Bug Fixes\n\n%b\n' "$FIXES"
  HAS_CONTENT=true
fi

if [ -n "$PERFORMANCE" ]; then
  printf '### ⚡ Performance\n\n%b\n' "$PERFORMANCE"
  HAS_CONTENT=true
fi

if [ -n "$INTERNAL" ]; then
  printf '### 🔩 Internal\n\n%b\n' "$INTERNAL"
  HAS_CONTENT=true
fi

if [ "$HAS_CONTENT" = false ]; then
  echo "_No user-facing changes in this release._"
fi
