#!/usr/bin/env python3
"""Builds the client override <client>/ro3emu/loc_en.tsv without data/loc or re/loc (build_locmap.js needs both).
   1. every zh_CN / zh_TW Localization string whose key has English in Localization_en;
   2. src/Ro3Emu.Loc/translations/*.tsv (ours, win over 1);
   3. with --merge-existing only: the file already at <out> (wins over 2). Off by default: every runtime batch now
      lives in translations/, and merging an installed file would undo fixes made there;
   4. job-select button: 选择职业 -> Choose This Job.
   usage: python3 tools/Loc/build_clientmap.py <out>/loc_en.tsv [--merge-existing]   (needs re/clientB/lua-src)
   then copy <out>/loc_en.tsv to <client>/ro3emu/loc_en.tsv."""
import re, sys, glob, os
root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
args = [a for a in sys.argv[1:] if not a.startswith('--')]
out = args[0]
CJK = re.compile('[\u2e80-\u2fdf\u3040-\u9fff\uf900-\ufaff]')
esc = lambda s: s.replace('\\', '\\\\').replace('\t', '\\t').replace('\r', '\\r').replace('\n', '\\n')

def dec(s):  # Lua decimal escapes -> utf-8
    b, i = bytearray(), 0
    while i < len(s):
        if s[i] == '\\' and i + 1 < len(s):
            m = re.match(r'\d{1,3}', s[i + 1:])
            if m: b.append(int(m.group()) & 255); i += 1 + len(m.group()); continue
            b.append({'n': 10, 'r': 13, 't': 9}.get(s[i + 1], ord(s[i + 1]) & 255)); i += 2; continue
        b += s[i].encode(); i += 1
    return b.decode('utf8', 'replace')

def table(lang):
    t = open(f'{root}/re/clientB/lua-src/Localization_{lang}.lua', encoding='utf8', errors='replace').read()
    return {m[1]: dec(m[2]) for m in re.finditer(
        r'L\d+_1 = (\d+)\nL\d+_1\._iID = L\d+_1\nL\d+_1 = "((?:[^"\\\n]|\\.)*)"\nL\d+_1\._kDes = L\d+_1', t)}

en = table('en')
m = {}
for lang in ('zh_TW', 'zh_CN'):
    for k, zh in table(lang).items():
        e = en.get(k)
        if e and e != 'None' and CJK.search(zh) and not CJK.search(e): m[esc(zh)] = esc(e)
def merge(path):
    for l in open(path, encoding='utf-8-sig'):
        l = l.rstrip('\n')
        if '\t' in l: a, b = l.split('\t', 1); m[a] = b
for f in sorted(glob.glob(f'{root}/src/Ro3Emu.Loc/translations/*.tsv')): merge(f)
if '--merge-existing' in sys.argv and os.path.exists(out): merge(out)
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
m['选择职业'] = 'Choose This Job'
with open(out, 'w', encoding='utf8') as o:
    o.writelines(f'{k}\t{v}\n' for k, v in m.items())
print(len(m), 'entries ->', out)
