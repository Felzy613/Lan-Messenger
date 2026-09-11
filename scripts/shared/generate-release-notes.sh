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
case "$PLATFORM" in
  combined) TAG_PREFIX="release-";        CURRENT_TAG="" ;;
  *)        TAG_PREFIX="${PLATFORM}-v";   CURRENT_TAG="${PLATFORM}-v${VERSION}" ;;
esac

if [ -n "$PREV_ARG" ]; then
  PREV_SHA="$PREV_ARG"
else
  # Auto-detect the *previous* release for this platform.
  #
  # Two filters matter, and both were missing:
  #
  #   isDraft — the build workflow creates its own draft release for this
  #   version before this script runs. GitHub does not create the git tag until
  #   a release is published, so a draft's tagName is not a resolvable git ref.
  #
  #   tagName != CURRENT_TAG — the newest entry is this version's own release,
  #   and `git log <this-version>..HEAD` is empty by construction.
  #
  # Together they are why every platform pre-release rendered as
  # "_No user-facing changes_": the range was built from a ref git could not
  # resolve, the fatal went to /dev/null, and an empty commit list looks
  # exactly like a release with nothing in it.
  PREV_TAG=$(gh release list --limit 100 \
    --json tagName,isDraft,createdAt \
    --jq "[.[]
           | select(.isDraft | not)
           | select(.tagName | startswith(\"${TAG_PREFIX}\"))
           | select(.tagName != \"${CURRENT_TAG}\")
          ] | sort_by(.createdAt) | reverse | .[0].tagName // empty" \
    2>/dev/null || true)

  if [ -n "$PREV_TAG" ]; then
    # Use the tag name directly — git resolves it to the tagged commit.
    # targetCommitish returns the branch name ("main"), not a SHA, so
    # `git log main..HEAD` silently produces zero commits.
    PREV_SHA="$PREV_TAG"
    echo "Generating notes since: $PREV_TAG" >&2
  else
    echo "::warning::No previous ${TAG_PREFIX}* release found; falling back to recent commits." >&2
  fi
fi

# A ref git cannot resolve must be loud and must fall back, never silently
# produce an empty changelog.
if [ -n "${PREV_SHA:-}" ] && ! git rev-parse --verify --quiet "${PREV_SHA}^{commit}" >/dev/null 2>&1; then
  echo "::warning::Release-notes boundary '${PREV_SHA}' is not a commit in this checkout" \
       "(unpublished draft, or a shallow clone — this job needs fetch-depth: 0)." \
       "Falling back to recent commits." >&2
  PREV_SHA=""
fi

# ── Collect commits ─────────────────────────────────────────────────────────
if [ -n "${PREV_SHA:-}" ]; then
  RANGE="${PREV_SHA}..HEAD"
  COMMITS=$(git log --no-merges --pretty="format:%s" "$RANGE" | head -80)
  if [ -z "$COMMITS" ]; then
    echo "::warning::No commits in range ${RANGE}." >&2
  fi
else
  RANGE="the last 20 commits"
  COMMITS=$(git log --no-merges --pretty="format:%s" -20 HEAD)
fi
echo "Commit range: ${RANGE}" >&2

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
  # Name the range that was compared. A bare "no changes" line is
  # indistinguishable from a broken boundary, which is how the empty notes went
  # unnoticed for as long as they did.
  echo "_No user-facing changes in this release (compared against ${RANGE})._"
fi
