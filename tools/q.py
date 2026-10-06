import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
#!/usr/bin/env python3
"""Query helper: substring/regex search over ALL_STRINGS.txt with section attribution.

Usage:
  python q.py <regex> [--ctx N] [--max M] [--file SUBSTR] [--cs]
"""
import re, sys, bisect, argparse, io

PATH = os.path.join(_paths.DISASM, "ALL_STRINGS.txt")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pattern")
    ap.add_argument("--ctx", type=int, default=0)
    ap.add_argument("--max", type=int, default=60)
    ap.add_argument("--file", default=None, help="only lines whose section header contains this (case-insensitive)")
    ap.add_argument("--cs", action="store_true")
    ap.add_argument("--nohead", action="store_true")
    ap.add_argument("--path", default=PATH)
    a = ap.parse_args()

    lines = io.open(a.path, encoding="utf-8", errors="replace").read().split("\n")
    heads = []  # (lineno0, title)
    for i, ln in enumerate(lines):
        if ln.startswith("===== FILE "):
            heads.append((i, ln))
    hpos = [h[0] for h in heads]

    flags = 0 if a.cs else re.IGNORECASE
    rx = re.compile(a.pattern, flags)

    n = 0
    for i, ln in enumerate(lines):
        if ln.startswith("===== FILE "):
            continue
        if not rx.search(ln):
            continue
        k = bisect.bisect_right(hpos, i) - 1
        sec = heads[k][1] if k >= 0 else "<none>"
        if a.file and a.file.lower() not in sec.lower():
            continue
        if not a.nohead:
            print(f"--- [{sec}]")
        for j in range(max(0, i - a.ctx), min(len(lines), i + a.ctx + 1)):
            mark = ">>" if j == i else "  "
            print(f"{mark}{j+1}: {lines[j]}")
        n += 1
        if n >= a.max:
            print(f"... (stopped at {a.max} matches)")
            break
    if n == 0:
        print("NO MATCHES")

if __name__ == "__main__":
    main()
