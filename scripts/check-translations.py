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
  UNVERIFIED a helper listed in DYNAMIC_CALLS that no longer translates the
             argument it is listed for — see below.
  UNWRAPPED  (warning) English-looking text on a driver-facing screen that is
             not inside a translation call. Heuristic, so it has false
             positives; read them, don't obey them.

MISSING, ORPHANED and UNVERIFIED fail the run. UNWRAPPED never does.

Dynamic call sites
------------------
Not every key sits inside a T(). The kiosk's completion screen writes

    html += dtRow('Gross:', gross.toLocaleString() + ' lb');

and dtRow translates the label. DYNAMIC_CALLS lists those helpers — function,
declaring file, argument position — so their literals are checked like every
other call site: reword one to 'Gross Weight:' without adding a catalog line
and it is reported MISSING, exactly as a direct T() would be.

The table is a claim about the code, so the checker re-derives it every run:
each listed helper must still hand that argument to T(), directly or by
forwarding it to another listed helper. One that stops is UNVERIFIED, not
silently trusted. Write a new helper of this shape and its strings turn up
ORPHANED until you list it — noisy on purpose. The alternative is what this
file exists to prevent: a translation that quietly stopped rendering.

Getting the Spanish written
---------------------------
    python3 scripts/check-translations.py --prompt

prints a ready-to-paste prompt: the strings that need Spanish, the whole
existing catalog as a glossary so the new ones match the vocabulary already
deployed, and the constraints that matter on a kiosk screen. Paste it into
Claude Code (or claude.ai), paste the returned lines into LangCatalog.cs.

That path deliberately needs no API key and costs nothing beyond a Claude
subscription. Read what comes back before pasting it — these strings tell a
driver standing next to a moving truck what to do.
"""

import argparse
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

# C# picks the key with a ternary in one place, and will again:
#     T(OtherCode == Lang.Spanish ? "Switch to Spanish" : "Switch to English")
CS_TERNARY = re.compile(
    r'(?:@L\[|\bL\[|\b_t\[|\bL\.T\(|\bT\()'
    r'\s*[^;\n]*?\?\s*"((?:[^"\\]|\\.)*)"\s*:\s*"((?:[^"\\]|\\.)*)"')

# Every literal in the file, used by the unwrapped-English sweep.
ANY_JS_LITERAL = re.compile(r"'((?:[^'\\\n]|\\.)*)'")

# Helpers that translate an argument on their caller's behalf, so the literal
# at that argument is a catalog key even though no T() encloses it:
# (function, file relative to web/Foundation.Web, argument index).
# verify_dynamic() re-checks each line against the source before the call sites
# behind it are trusted.
#
# showDemoTicket earns its line by anchoring showComplete: the literal is
# written at showComplete('Weigh In', ...) and translated one hop later.
# Deliberately NOT listed: row() in the office views, same shape, no
# translation — those screens are English by design.
DYNAMIC_CALLS = [
    ("dtRow",          "Views/Kiosk/Index.cshtml", 0),
    ("showDemoTicket", "Views/Kiosk/Index.cshtml", 0),
    ("showComplete",   "Views/Kiosk/Index.cshtml", 0),
]

ARG_LITERAL = re.compile(r"""^'((?:[^'\\]|\\.)*)'$|^"((?:[^"\\]|\\.)*)"$""")


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
    """Explicit call-site keys -> the files that reference them."""
    calls = {}
    for path in files:
        src = read(path)
        rel = os.path.relpath(path, REPO)
        found = [unescape_js(m) for m in JS_CALL.findall(src)]
        for a, b in JS_TERNARY.findall(src):
            found += [unescape_js(a), unescape_js(b)]
        for a, b in CS_TERNARY.findall(src):
            found += [unescape_cs(a), unescape_cs(b)]
        found += [unescape_cs(m) for m in RAZOR_CALL.findall(src)]
        for key in found:
            calls.setdefault(key, set()).add(rel)
    return calls


# --- dynamic call sites ----------------------------------------------------

def mask(src):
    """src with string bodies and comments blanked, positions preserved.

    Brace and paren matching runs on this copy so a '{' inside a string or a
    ')' inside a comment cannot throw the count off; the offsets it returns
    still index the real source.
    """
    out = list(src)
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c in "'\"`":
            j = i + 1
            while j < n and src[j] != c:
                j += 2 if src[j] == "\\" else 1
            for k in range(i, min(j + 1, n)):
                out[k] = " "
            i = j + 1
        elif src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            for k in range(i, j):
                out[k] = " "
            i = j
        elif src.startswith("/*", i):
            j = src.find("*/", i)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                out[k] = " "
            i = j
        else:
            i += 1
    return "".join(out)


def find_function(src, masked, name):
    """(parameter names, body source) for `function name(...) {...}`, or None."""
    m = re.search(r"\bfunction\s+" + re.escape(name) + r"\s*\(([^)]*)\)\s*\{", masked)
    if not m:
        return None
    params = [p.strip() for p in src[m.start(1):m.end(1)].split(",") if p.strip()]
    open_brace, depth = m.end() - 1, 0
    for i in range(open_brace, len(masked)):
        if masked[i] == "{":
            depth += 1
        elif masked[i] == "}":
            depth -= 1
            if depth == 0:
                return params, src[open_brace + 1:i]
    return params, src[open_brace + 1:]


def split_args(src, masked, open_paren):
    """Source of each argument of the call whose '(' sits at open_paren."""
    args, depth, start = [], 0, open_paren + 1
    for i in range(open_paren, len(masked)):
        c = masked[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth == 0:
                args.append(src[start:i])
                return [a.strip() for a in args]
        elif c == "," and depth == 1:
            args.append(src[start:i])
            start = i + 1
    return []


def call_sites(src, masked, name):
    """Argument lists of every call to `name` that is not its declaration."""
    for m in re.finditer(r"\b" + re.escape(name) + r"\s*\(", masked):
        if re.search(r"\bfunction\s+$", masked[:m.start()]):
            continue
        yield split_args(src, masked, m.end() - 1)


def verify_dynamic():
    """Which DYNAMIC_CALLS lines the source still supports, and why not.

    A helper passes verification when the named argument reaches T() in its
    own body, or when it is forwarded — at the matching position — to another
    helper that passes. The forwarding case is resolved to a fixed point, so a
    chain of any depth works as long as every hop is listed.
    """
    decls, broken = {}, {}
    for name, rel, idx in DYNAMIC_CALLS:
        path = os.path.join(WEB, *rel.split("/"))
        if not os.path.exists(path):
            broken[(name, idx)] = f"{rel} no longer exists"
            continue
        src = read(path)
        found = find_function(src, mask(src), name)
        if not found:
            broken[(name, idx)] = f"no function {name}(...) in {rel}"
            continue
        params, body = found
        if idx >= len(params):
            broken[(name, idx)] = (
                f"{name}() in {rel} takes {len(params)} argument(s), "
                f"so there is no #{idx} to translate")
            continue
        decls[(name, idx)] = (params[idx], body, rel)

    verified, changed = set(), True
    while changed:
        changed = False
        for key, (param, body, _rel) in decls.items():
            if key in verified:
                continue
            masked = mask(body)
            direct = re.search(
                r"\bTF?\(\s*" + re.escape(param) + r"\s*[,)]", masked)
            if direct or forwards(body, masked, param, verified):
                verified.add(key)
                changed = True

    for key, (param, body, rel) in decls.items():
        if key not in verified:
            broken[key] = (f"{key[0]}() in {rel} no longer passes its argument "
                           f"#{key[1]} ({param}) to T()")
    return verified, broken


def forwards(body, masked, param, verified):
    """True if body hands `param` to an already-verified helper's slot."""
    for name, idx in verified:
        for args in call_sites(body, masked, name):
            if idx < len(args) and args[idx] == param:
                return True
    return False


def dynamic_keys(verified):
    """Literal arguments passed to a verified helper -> the files doing so."""
    keys = {}
    for name, rel, idx in DYNAMIC_CALLS:
        if (name, idx) not in verified:
            continue
        path = os.path.join(WEB, *rel.split("/"))
        src = read(path)
        for args in call_sites(src, mask(src), name):
            if idx >= len(args):
                continue
            m = ARG_LITERAL.match(args[idx])
            if not m:
                continue          # a runtime value, not a catalog key
            single, double = m.groups()
            key = unescape_js(single) if single is not None else unescape_cs(double)
            keys.setdefault(key, set()).add(os.path.relpath(path, REPO))
    return keys


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


PROMPT_HEADER = """\
Translate UI strings into Spanish for Foundation, a truck-scale management
system used at grain elevators and scale houses in the United States.

Who reads these: truck drivers standing at a weigh scale, on a 1280x800
touchscreen kiosk (often in direct sun) or on their own phone. Many are native
Spanish speakers from Mexico and Central America. Use Latin American Spanish.

Rules:

1. Keep {0}, {1} placeholders exactly as written. Reorder them if Spanish word
   order needs it, but never drop or renumber one.
2. Preserve the capitalisation style. ALL CAPS strings are the large
   call-to-action text on the kiosk and must come back ALL CAPS. Title Case
   stays Title Case. lowercase stays lowercase.
3. Keep it short. These sit in fixed-width buttons, headers and overlays; a
   translation much longer than the English overflows the layout.
4. Use direct imperative address (usted implied, not written out).
5. Match the glossary below for any term that appears in it. Those strings are
   already deployed on live kiosks and the vocabulary has to stay consistent --
   a screen that says "Boleta" in one place and "Ticket" in another is worse
   than one that is all English.
6. If a string is genuinely ambiguous without more context, translate it and
   add a trailing comment saying what you assumed. Do not guess silently.

Return ONLY lines in this exact C# format, ready to paste into
web/Foundation.Web/Services/LangCatalog.cs, in the same order as the input:

    ["English source"] = "Spanish",

Escape any double quote inside a value as \\" -- the target is a C# file.
"""


def emit_prompt(pairs, missing):
    """Ready-to-paste prompt: what needs Spanish, plus the vocabulary to match."""
    if not missing:
        print("Nothing to translate — every referenced string is in the catalog.")
        print()
        print("This mode lists strings the code asks for that the catalog lacks.")
        print("To re-translate something that already has Spanish, edit its line")
        print("in LangCatalog.cs directly.")
        return 0

    print(PROMPT_HEADER)
    print("=== GLOSSARY: already translated, match these terms ===")
    print()
    for english, spanish in pairs:
        print(f"{english}  ->  {spanish}")
    print()
    plural = "STRING" if len(missing) == 1 else "STRINGS"
    print(f"=== TRANSLATE {'THIS' if len(missing) == 1 else 'THESE'} "
          f"{len(missing)} {plural} ===")
    print()
    for key in sorted(missing):
        print(key)
    print()
    return 0


def main():
    parser = argparse.ArgumentParser(
        description="Check the Spanish catalog against the code that uses it.")
    parser.add_argument(
        "--prompt", action="store_true",
        help="print a paste-ready translation prompt for the missing strings "
             "(glossary included) instead of the usual report")
    args = parser.parse_args()

    pairs, catalog_keys, dupes = load_catalog()
    calls = scan(source_files())

    verified, broken = verify_dynamic()
    dynamic = dynamic_keys(verified)
    for key, files in dynamic.items():
        calls.setdefault(key, set()).update(files)

    missing = {k: v for k, v in calls.items() if k not in catalog_keys}
    orphaned = sorted(catalog_keys - set(calls))

    if args.prompt:
        return emit_prompt(pairs, missing)

    unwrapped = sweep_unwrapped(catalog_keys)

    print(f"catalog:    {len(pairs)} entries")
    print(f"call sites: {len(calls)} distinct strings "
          f"({len(dynamic)} of them through a helper in DYNAMIC_CALLS)")
    print()

    failed = False

    if broken:
        failed = True
        print(f"UNVERIFIED ({len(broken)}) — DYNAMIC_CALLS no longer matches the code.")
        print("Until this is fixed the strings behind these helpers are unchecked:")
        for (name, idx), why in sorted(broken.items()):
            print(f"    {name}() arg #{idx}: {why}")
        print()

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
        print("the new English text, or delete the entry if the string is gone. If")
        print("the key is reached through a helper, add the helper to DYNAMIC_CALLS.")
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
