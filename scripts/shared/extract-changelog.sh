#!/usr/bin/env bash
# Usage: extract-changelog.sh <version> [changelog-file]
# Prints the body of the ## [<version>] section to stdout.
# Exits 0 and prints nothing if the version is not found.
set -euo pipefail

VERSION="${1:-}"
FILE="${2:-CHANGELOG.md}"

if [[ -z "$VERSION" ]]; then
  echo "Usage: $0 <version> [changelog-file]" >&2
  exit 1
fi

if [[ ! -f "$FILE" ]]; then
  exit 0
fi

awk -v ver="[${VERSION}]" '
  /^## \[/ { in_ver = ($2 == ver); next }
  in_ver   { print }
' "$FILE"
