#!/bin/zsh
# Extracts every localized string catalog (and the settings schema) from the
# Vorssaint macOS sources into JSON. Read-only with respect to the repo.
#
# Usage: tools/run.sh <path-to-vorssaint-utils> <output-dir>
# Needs: Xcode or Command Line Tools (same toolchain build.sh uses), python3.
set -euo pipefail
REPO="$(cd "$1" && pwd)"
OUT="$(mkdir -p "$2" && cd "$2" && pwd)"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
WORK="$OUT/work"
mkdir -p "$WORK/gen" "$WORK/obj-strings" "$WORK/obj-schema" "$WORK/schema" "$WORK/tmp"
export TMPDIR="$WORK/tmp/"

# Same SDK choice as build.sh: the pinned macOS 26 SDK when present.
PINNED_SDK="/Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk"
COMPAT=()
if [[ -z "${DEVELOPER_DIR:-}" && -d "$PINNED_SDK" ]]; then
    SDK="$PINNED_SDK"
    COMPAT=(-Xfrontend -interface-compiler-version -Xfrontend 6.3.2)
else
    SDK="$(xcrun --show-sdk-path)"
fi

python3 -I "$TOOLS/generate.py" "$REPO" "$WORK/gen"
cp "$TOOLS/Stubs.swift" "$WORK/gen/Stubs.swift"
cp "$TOOLS/settings_schema_main.swift" "$WORK/schema/main.swift"

compile() {  # $1 = module name, $2 = main.swift, $3 = object dir, $4 = output binary
    local list="$WORK/gen/$1.sources" map="$WORK/gen/$1.ofm.json"
    { cat "$WORK/gen/sources.txt"; echo "$WORK/gen/Stubs.swift"; echo "$2"; } > "$list"
    python3 -I - "$list" "$3" "$map" <<'EOF'
import json, sys
sources = [l.strip() for l in open(sys.argv[1]) if l.strip()]
objects = sys.argv[2]
m = {"": {"swift-dependencies": f"{objects}/master.swiftdeps"}}
for s in sources:
    m[s] = {"object": f"{objects}/" + s.replace("/", "__").removesuffix(".swift") + ".o"}
json.dump(m, open(sys.argv[3], "w"))
EOF
    swiftc -Onone -enable-batch-mode -j "$(sysctl -n hw.logicalcpu)" -module-name "$1" \
        -output-file-map "$map" -target arm64-apple-macosx14.0 -sdk "$SDK" "${COMPAT[@]}" \
        -I "$REPO/Sources/VMStatisticsCompat" -I "$REPO/Sources/HIDEventSystem" \
        $(cat "$list") -o "$4"
}

compile LocExtract "$WORK/gen/main.swift" "$WORK/obj-strings" "$WORK/locextract"
compile SchemaExtract "$WORK/schema/main.swift" "$WORK/obj-schema" "$WORK/schemaextract"

"$WORK/locextract" "$OUT/strings.json"
"$WORK/schemaextract" "$OUT/settings_schema.json"
python3 -I "$TOOLS/flatten.py" "$OUT/strings.json" "$OUT/i18n" | tail -3
echo "done: $OUT"
