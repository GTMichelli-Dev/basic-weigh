#!/usr/bin/env python3
"""
Check the Spanish catalog against the code that uses it.

    python3 scripts/check-translations.py

Exits non-zero when something would silently render in English on a Spanish
kiosk, so it can be wired into a PR check later if that becomes worth doing.

Why this exists
---------------
Services/LangCatalog.cs is keyed by the *English source text* of each string.
That is deliberate — a string with no entry falls back to readable English
instead of showing a driver a raw key like KIOSK_IDLE_MSG. The cost is that
rewording an English string silently orphans its translation:

    - @L["Place Truck on Scale"]        <- catalog has this
    + @L["Pull Truck onto Scale"]       <- catalog does not

No compile error, no warning; that line just reverts to English on every
Spanish screen. Nobody at a desk notices. A driver in the yard does.

What it reports
---------------
  MISSING    a T()/TF()/@L[] call naming a string the catalog lacks.
             Renders English on Spanish screens.
  ORPHANED   a catalog entry whose English text appears nowhere in the code.
             Usually the other half of a reword — fix the key or delete it.
  UNWRAPPED  (warning) English-looking text on a driver-facing screen that is
             not inside a translation call. Heuristic, so it has false
             positives; read them, don't obey them.

MISSING and ORPHANED fail the run. UNWRAPPED never does.
"""

import io
import os
import re
import sys
from glob import glob

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WEB = os.path.join(REPO, "web", "Foundation.Web")
CATALOG = os.path.join(WEB, "Services", "LangCatalog.cs")

# Everything that may legitimately reference a catalog key.
SEARCH_GLOBS = [
    os.path.join(WEB, "Views", "**", "*.cshtml"),
    os.path.join(WEB, "Controllers", "*.cs"),
    os.path.join(WEB, "Services", "*.cs"),
]

# The bilingual screens. Only these are swept for unwrapped English — the
# office and admin pages are English by design and would bury the signal.
DRIVER_VIEWS = [
    os.path.join(WEB, "Views", "Kiosk", "Index.cshtml"),
    os.path.join(WEB, "Views", "Mobile", "Index.cshtml"),
    os.path.join(WEB, "Views", "SignaturePad", "Index.cshtml"),
    os.path.join(WEB, "Views", "Ticket", "Print.cshtml"),
    os.path.join(WEB, "Views", "Ticket", "_TicketBody.cshtml"),
]

CATALOG_ENTRY = re.compile(
    r'^\s*\["((?:[^"\\]|\\.)*)"\]\s*=\s*"((?:[^"\\]|\\.)*)",\s*$', re.M)

# Explicit call sites:  T('x')  TF('x', ...)  @L["x"]  L.T("x")  _t["x"]
JS_CALL = re.compile(r"\bTF?\(\s*'((?:[^'\\]|\\.)*)'")
JS_TERNARY = re.compile(r"\bTF?\(\s*[^)]*?\?\s*'((?:[^'\\]|\\.)*)'\s*:\s*'((?:[^'\\]|\\.)*)'")
RAZOR_CALL = re.compile(r'(?:@L|\bL|\b_t)\s*(?:\[|\.T\()\s*"((?:[^"\\]|\\.)*)"')

# Every literal in the file, used only to decide whether a catalog entry is
# still referenced somehow — robust to call shapes this script cannot parse
# (a key passed through a helper, built in a ternary, held in a variable).
ANY_JS_LITERAL = re.compile(r"'((?:[^'\\\n]|\\.)*)'")
ANY_CS_LITERAL = re.compile(r'"((?:[^"\\\n]|\\.)*)"')


def unescape_js(s):
    return s.replace("\\'", "'").replace("\\\\", "\\")


def unescape_cs(s):
    return s.replace('\\"', '"').replace("\\\\", "\\")


def read(path):
    return io.open(path, encoding="utf-8-sig").read()


def load_catalog():
    src = read(CATALOG)
    pairs = [(unescape_cs(k), unescape_cs(v)) for k, v in CATALOG_ENTRY.findall(src)]
    keys = [k for k, _ in pairs]
    dupes = sorted({k for k in keys if keys.count(k) > 1})
    return pairs, set(keys), dupes


def source_files():
    seen = []
    for pattern in SEARCH_GLOBS:
        for path in sorted(glob(pattern, recursive=True)):
            if os.path.abspath(path) != os.path.abspath(CATALOG):
                seen.append(path)
    return seen


def scan(files):
    """(explicit call-site keys -> files, every literal seen anywhere)"""
    calls, literals = {}, set()
    for path in files:
        src = read(path)
        rel = os.path.relpath(path, REPO)
        found = [unescape_js(m) for m in JS_CALL.findall(src)]
        for a, b in JS_TERNARY.findall(src):
            found += [unescape_js(a), unescape_js(b)]
        found += [unescape_cs(m) for m in RAZOR_CALL.findall(src)]
        for key in found:
            calls.setdefault(key, set()).add(rel)
        literals.update(unescape_js(m) for m in ANY_JS_LITERAL.findall(src))
        literals.update(unescape_cs(m) for m in ANY_CS_LITERAL.findall(src))
    return calls, literals


# --- unwrapped-English heuristic -------------------------------------------

CODEISH = re.compile(
    r'''^(?:[#.\[]         # jQuery selectors
        |/                 # paths and urls
        |https?:
        |[a-z-]+:[a-z0-9-] # css decls, ids like "serviceId:scaleId"
        )''', re.X | re.I)
CSSISH = re.compile(r'[{};:]|^(?:[a-z-]+)$|px|rgba?\(|#[0-9a-f]{3,6}', re.I)
# "weight-ok weight-motion", "w-ok w-motion w-error" — all-lowercase tokens
# with a hyphen in them is a CSS class list, never a sentence. Deliberately
# narrow: plenty of real UI copy is lowercase ("whole number", "connecting…"),
# so anything without a hyphen still gets reported.
CLASS_LIST = re.compile(r'^[a-z][a-z0-9-]*(?:\s+[a-z][a-z0-9-]*)+\s*$')

# Code and internal strings that trip the heuristic and always will. Each is
# here because it is not user-visible, not because it is inconvenient.
NOT_COPY = {
    "use strict",              # directive prologue
    "warn err",                # banner CSS classes
    "btn ",                    # class-name prefix, concatenated
    "wheel touchmove scroll",  # jQuery event list
    "upload failed",           # internal Error(), replaced by a T()d alert
}


def looks_like_copy(s):
    if len(s) < 3 or CODEISH.match(s) or not re.search(r"[A-Za-z]{3}", s):
        return False
    if s in NOT_COPY or "@" in s:      # @ means a Razor expression, not text
        return False
    if CLASS_LIST.match(s) and "-" in s:
        return False
    if '="' in s:                      # a fragment of markup being concatenated
        return False
    if " " not in s and not re.match(r"^[A-Z][a-z]+$", s):
        return False
    if CSSISH.search(s) and "<" not in s:
        return False
    if s.startswith("<") and " " not in re.sub(r"<[^>]*>", "", s):
        return False
    return True


def sweep_unwrapped(catalog_keys):
    """English-looking literals on driver screens that no call site wraps."""
    hits = {}
    for path in DRIVER_VIEWS:
        if not os.path.exists(path):
            continue
        src = read(path)
        rel = os.path.relpath(path, REPO)
        js = src[src.index("<script>"):] if "<script>" in src else src
        js = re.sub(r"<style>.*?</style>", "", js, flags=re.S)
        js = re.sub(r"^\s*//.*$", "", js, flags=re.M)
        js = re.sub(r"/\*.*?\*/", "", js, flags=re.S)
        for m in ANY_JS_LITERAL.finditer(js):
            s = unescape_js(m.group(1))
            # Already a catalog key: it is translated at the point of display
            # even when the literal itself sits outside a T() call.
            if s in catalog_keys or not looks_like_copy(s):
                continue
            before = js[max(0, m.start() - 60):m.start()]
            if re.search(r"\bTF?\(|\b(?:dtRow|row)\($", before):
                continue
            hits.setdefault(rel, []).append(s)
    return hits


def main():
    pairs, catalog_keys, dupes = load_catalog()
    calls, literals = scan(source_files())

    missing = {k: v for k, v in calls.items() if k not in catalog_keys}
    orphaned = sorted(catalog_keys - literals)
    unwrapped = sweep_unwrapped(catalog_keys)

    print(f"catalog:    {len(pairs)} entries")
    print(f"call sites: {len(calls)} distinct strings")
    print()

    failed = False

    if dupes:
        failed = True
        print(f"DUPLICATE KEYS ({len(dupes)}) — the dictionary initialiser throws at startup:")
        for k in dupes:
            print(f'    "{k}"')
        print()

    if missing:
        failed = True
        print(f"MISSING ({len(missing)}) — referenced in code, absent from the catalog.")
        print("These render in English on Spanish screens. Add a line to LangCatalog.cs:")
        for k in sorted(missing):
            print(f'    ["{k}"] = "...",')
            print(f'        used in: {", ".join(sorted(missing[k]))}')
        print()

    if orphaned:
        failed = True
        print(f"ORPHANED ({len(orphaned)}) — in the catalog, referenced nowhere.")
        print("Usually the other half of a reworded string: update the key to match")
        print("the new English text, or delete the entry if the string is gone.")
        for k in orphaned:
            print(f'    ["{k}"]')
        print()

    if unwrapped:
        total = sum(len(v) for v in unwrapped.values())
        print(f"UNWRAPPED ({total}) — warning only, heuristic, expect false positives.")
        print("English-looking text on a driver screen with no translation call:")
        for rel, items in unwrapped.items():
            for s in dict.fromkeys(items):
                print(f"    {rel}: {s!r}")
        print()

    if failed:
        print("FAIL — see above.")
        return 1
    print("OK — every referenced string is in the catalog, and every catalog")
    print("entry is still referenced.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
