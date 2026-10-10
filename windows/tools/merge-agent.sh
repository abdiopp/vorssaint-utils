#!/usr/bin/env bash
# Copies one agent's owned folders from its private work copy into this tree.
#   tools/merge-agent.sh <work-copy-windows-dir> <Module> [<Module> ...]
# For each module it syncs the module folders in every project, its strings
# files, tests and docs. Nothing outside those paths is touched.
set -euo pipefail
SRC="${1:?usage: merge-agent.sh <work-copy-windows-dir> <Module>...}"; shift
DST="$(cd "$(dirname "$0")/.." && pwd)"
[ -d "$SRC/src" ] || { echo "Not a work copy: $SRC" >&2; exit 1; }

sync_dir() {
  local rel="$1"
  if [ -d "$SRC/$rel" ]; then
    mkdir -p "$DST/$rel"
    rsync -a --delete --exclude bin --exclude obj "$SRC/$rel/" "$DST/$rel/"
    echo "  synced $rel"
  fi
}

for MODULE in "$@"; do
  lower="$(echo "${MODULE:0:1}" | tr '[:upper:]' '[:lower:]')${MODULE:1}"
  echo "Module $MODULE"
  for project in Rivet.App/Features Rivet.Core Rivet.Imaging Rivet.Platform.Windows Rivet.Platform.Fake; do
    sync_dir "src/$project/$MODULE"
  done
  for tests in Rivet.Core.Tests Rivet.Imaging.Tests Rivet.App.Tests; do
    sync_dir "tests/$tests/$MODULE"
  done
  for f in "$SRC"/src/Rivet.Core/Resources/i18n-win/"$lower".*.json; do
    [ -e "$f" ] && cp "$f" "$DST/src/Rivet.Core/Resources/i18n-win/" && echo "  copied $(basename "$f")"
  done
  if [ -f "$SRC/docs/modules/$lower.md" ]; then
    mkdir -p "$DST/docs/modules" && cp "$SRC/docs/modules/$lower.md" "$DST/docs/modules/" && echo "  copied docs/modules/$lower.md"
  fi
done
