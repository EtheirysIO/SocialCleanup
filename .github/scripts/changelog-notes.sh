#!/usr/bin/env bash
# Usage: changelog-notes.sh VERSION CHANGELOG OUT
# Writes the "## VERSION" section of CHANGELOG (up to the next "## " heading) to OUT.
# Fails when the section is missing or empty: a release without release notes is not published.
set -euo pipefail

version="$1"
changelog="$2"
out="$3"

awk -v v="$version" '
  $0 ~ "^## " { if (found) exit; if ($2 == v && NF == 2) { found = 1; next } }
  found { print }
' "$changelog" > "$out"

if ! grep -q '[^[:space:]]' "$out"; then
  echo "::error::CHANGELOG.md has no '## $version' section (or it is empty)"
  exit 1
fi
