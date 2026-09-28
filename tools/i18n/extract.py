# -*- coding: utf-8 -*-
"""Collect Korean UI strings from src (tests excluded) and report which keys each
translation table (assets/i18n/en.json, ja.json, zh.json) is missing.

    python tools/i18n/extract.py            # report only
    python tools/i18n/extract.py --write    # also rewrite assets/i18n/source.json

Interpolation holes become {0}, {1}, ...; nested literals inside holes are collected too."""
import json
import pathlib
import re
import sys

repo = pathlib.Path(__file__).resolve().parents[2]
ESC = {"n": "\n", "t": "\t", '"': '"', "\\": "\\", "r": "", "0": "\0", "'": "'"}
HANGUL = re.compile(r"[가-힣]")


def scan(text):
    """Yield (literal_text_with_placeholders) for every string literal, recursing into holes."""
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i); i = n if j < 0 else j; continue
        if text.startswith("/*", i):
            j = text.find("*/", i); i = n if j < 0 else j + 2; continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            i = j + 1; continue
        prefix = ""
        if c in "$@" and i + 1 < n and (text[i + 1] == '"' or (text[i + 1] in "$@" and i + 2 < n and text[i + 2] == '"')):
            prefix = text[i:text.index('"', i)]
            i = text.index('"', i)
            c = '"'
        if c == '"':
            if text.startswith('"""', i):
                j = text.find('"""', i + 3); i = n if j < 0 else j + 3; continue
            value, i = read(text, i + 1, "$" in prefix, "@" in prefix)
            yield from value
            continue
        i += 1


def read(text, i, interpolated, verbatim):
    out, results, holes = [], [], 0
    n = len(text)
    while i < n:
        c = text[i]
        if verbatim and c == '"':
            if i + 1 < n and text[i + 1] == '"':
                out.append('"'); i += 2; continue
            i += 1; break
        if not verbatim and c == '"':
            i += 1; break
        if not verbatim and c == "\\":
            e = text[i + 1]
            if e == "u":
                out.append(chr(int(text[i + 2:i + 6], 16))); i += 6
            else:
                out.append(ESC.get(e, e)); i += 2
            continue
        if interpolated and c == "{":
            if i + 1 < n and text[i + 1] == "{":
                out.append("{"); i += 2; continue
            # Hole: scan to the matching brace, collecting nested literals.
            depth, j = 1, i + 1
            start = j
            while j < n and depth:
                if text[j] == '"' or (text[j] in "$@" and j + 1 < n and text[j + 1] == '"'):
                    sub_start = j
                    k = j
                    while text[k] != '"':
                        k += 1
                    pre = text[sub_start:k]
                    nested, j = read(text, k + 1, "$" in pre, "@" in pre)
                    results.extend(nested)
                    continue
                if text[j] == "{": depth += 1
                elif text[j] == "}": depth -= 1
                j += 1
            out.append("{" + str(holes) + "}"); holes += 1
            i = j
            continue
        if interpolated and c == "}" and i + 1 < n and text[i + 1] == "}":
            out.append("}"); i += 2; continue
        out.append(c); i += 1
    s = "".join(out)
    if HANGUL.search(s):
        results.append(s)
    return results, i


found = {}
for f in sorted((repo / "src").rglob("*.cs")):
    if any(p in ("Tests", "bin", "obj") for p in f.parts):
        continue
    for s in scan(f.read_text(encoding="utf-8")):
        if s.startswith("menu:") or s.startswith("morupixel:"):
            continue
        found.setdefault(s, str(f.relative_to(repo / "src")).replace("\\", "/"))

source = repo / "assets/i18n/source.json"
# Language names and sample file paths are shown as they are.
items = [{"ko": k, "file": v} for k, v in found.items() if k not in ("한국어",) and not re.match(r"^C:\\", k)]
if "--write" in sys.argv:
    source.write_text(json.dumps(items, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
keys = {i["ko"] for i in items}
status = 0
for code in ("en", "ja", "zh"):
    table = json.loads((repo / f"assets/i18n/{code}.json").read_text(encoding="utf-8"))["strings"]
    missing = sorted(keys - set(table))
    print(f"{code}: {len(table)} entries, {len(missing)} missing")
    for key in missing[:20]:
        print("   ", json.dumps(key, ensure_ascii=False))
    status |= bool(missing)
sys.exit(status)
