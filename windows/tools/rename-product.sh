#!/usr/bin/env bash
# Renames the working codename everywhere in windows/: the product identity in
# Directory.Build.props, namespaces, project and folder names.
#   tools/rename-product.sh NewName
# Review the diff afterwards; strings shown to users already use the product
# name from Directory.Build.props, so they need no change.
set -euo pipefail
NEW="${1:?usage: rename-product.sh NewName}"
OLD="Rivet"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if ! [[ "$NEW" =~ ^[A-Z][A-Za-z0-9]+$ ]]; then
  echo "The name must be PascalCase letters and digits." >&2
  exit 1
fi

# Contents first (code, projects, docs), skipping build output.
grep -rlI --exclude-dir={bin,obj,artifacts,.git} "$OLD" . | while read -r f; do
  perl -pi -e "s/\b${OLD}\b/${NEW}/g; s/\b${OLD}\./${NEW}./g" "$f"
done

# Then file and folder names, deepest first.
find . -depth -name "*${OLD}*" -not -path "*/bin/*" -not -path "*/obj/*" | while read -r p; do
  mv "$p" "$(dirname "$p")/$(basename "$p" | sed "s/${OLD}/${NEW}/g")"
done
echo "Renamed ${OLD} → ${NEW}. Build and run the tests to confirm."
