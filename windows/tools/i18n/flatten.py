#!/usr/bin/env python3
"""Flatten strings.json (catalog -> lang -> key -> value) into one JSON file
per language with dotted keys ("Strings.menuSettings", "hub.pageTitle"),
plus _meta.json describing each key.

Values are kept verbatim (Apple printf syntax: %@, %d, %1$@, %.2f, %%) by
default, because a "%" inside a value is not always a placeholder: some
captions document literal tokens ("%y, %mo, %#") or contain "100 %". The
Windows side formats with a printf-compatible helper only at the call sites
that used String(format:). Pass --icu to also convert likely format strings to
positional {0} placeholders.

Usage: python3 -I flatten.py strings.json <out-dir> [--icu]
"""
import json
import re
import sys
from pathlib import Path

src = json.loads(Path(sys.argv[1]).read_text())
out = Path(sys.argv[2])
icu = "--icu" in sys.argv[3:]
out.mkdir(parents=True, exist_ok=True)

TOKEN = re.compile(r"%(?:(\d+)\$)?[-+ #0']*\d*(?:\.(\d+))?(hh|ll|[hljztLq])?([@dius]|f)|%%")
# Values whose "%" is literal text, found by comparing every translation
# against English. Extend this list when a new caption documents tokens.
LITERAL_PERCENT = {
    "screenshot.fileNamePatternCaption", "screenshot.subfolderCaption",
    "Strings.mixerResetTooltip", "Strings.memoryStylePercent",
    "notchActivities.accessoryDescription",
}

def signature(text):
    args, sequential = {}, 0
    for m in TOKEN.finditer(text):
        if m.group(0) == "%%":
            continue
        position, precision, _, conv = m.groups()
        index = int(position) - 1 if position else sequential
        if not position:
            sequential += 1
        kind = {"@": "string", "s": "string", "d": "int", "i": "int", "u": "int"}.get(conv, "float")
        args[index] = kind + (f".{precision}" if kind == "float" and precision else "")
    return [args[i] for i in sorted(args)]

def to_icu(text):
    sequential = 0
    def repl(m):
        nonlocal sequential
        if m.group(0) == "%%":
            return "%"
        position = m.group(1)
        index = int(position) - 1 if position else sequential
        if not position:
            sequential += 1
        return "{" + str(index) + "}"
    return TOKEN.sub(repl, text)

def dotted(catalog, field):
    return f"{catalog.removeprefix('FeatureStrings.')}.{field}"

languages = list(next(iter(src.values())).keys())
meta = {}
for catalog, tables in src.items():
    for field, english in tables["en-US"].items():
        key = dotted(catalog, field)
        args = signature(english)
        likely = key not in LITERAL_PERCENT and (bool(args) or "format" in field.lower())
        same = [l for l in languages if l != "en-US" and tables[l].get(field) == english]
        meta[key] = {"catalog": catalog, "field": field, "isFormat": likely,
                     "args": args if likely else [], "sameAsEnglishIn": same}

problems = 0
for language in languages:
    flat = {}
    for catalog, tables in src.items():
        for field, value in tables[language].items():
            key = dotted(catalog, field)
            info = meta[key]
            if info["isFormat"] and signature(value) != info["args"]:
                problems += 1
                print(f"placeholder mismatch {language} {key}: {signature(value)} vs {info['args']}")
            flat[key] = to_icu(value) if (icu and info["isFormat"]) else value
    (out / f"{language}.json").write_text(json.dumps(flat, ensure_ascii=False, indent=1, sort_keys=True))

(out / "_meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1, sort_keys=True))
formats = sum(1 for m in meta.values() if m["isFormat"])
print(f"{len(languages)} languages, {len(meta)} keys each, {formats} format strings, {problems} placeholder mismatches")
