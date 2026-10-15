#!/usr/bin/env python3
"""Placeholder / tag parity between the Chinese key and the English text of every translation line.
   Checked tokens: printf (%s %d %.1f %%), {0} {0:00}, ${1} @{1}, rich-text tags <...>, escaped \\n / \\t.
   Tags compare by name (<color=#fff> and </color>); the English may not add or drop any token.
   usage: python3 tools/Loc/check_placeholders.py [file.tsv ...]   (default: src/Ro3Emu.Loc/translations/*.tsv)
   exit 1 when a line fails."""
import glob, os, re, sys
from collections import Counter

root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
TOKEN = re.compile(r'%%|%(?:\d+\$)?[-+0#]*\d*(?:\.\d+)?[sdfiuxX]|[$@]?\{\d+(?::[^}]*)?\}|</?[A-Za-z][^<>]*>|\\[nt]')


def tokens(s):
    out = Counter()
    for t in TOKEN.findall(s):
        if t.startswith('<'):
            # <color=#ff0000> and <color=#00ff00> are the same token; the value may change, the tag may not
            m = re.match(r'</?\s*([A-Za-z]+)', t)
            t = ('</' if t.startswith('</') else '<') + m.group(1).lower() + '>'
        out[t] += 1
    return out


def check(path):
    bad = 0
    for n, line in enumerate(open(path, encoding='utf-8-sig'), 1):
        line = line.rstrip('\n')
        if not line or line.startswith('#') or '\t' not in line:
            continue
        zh, en = line.split('\t', 1)
        a, b = tokens(zh), tokens(en)
        if a != b:
            bad += 1
            miss, extra = a - b, b - a
            print(f'{os.path.relpath(path, root)}:{n}: missing {dict(miss)} extra {dict(extra)}\n    {zh}\n    {en}')
    return bad


files = sys.argv[1:] or sorted(glob.glob(f'{root}/src/Ro3Emu.Loc/translations/*.tsv'))
total = sum(check(f) for f in files)
lines = sum(1 for f in files for l in open(f, encoding='utf-8-sig') if '\t' in l and not l.startswith('#'))
print(f'{len(files)} files, {lines} lines, {total} with placeholder/tag mismatches')
sys.exit(1 if total else 0)
