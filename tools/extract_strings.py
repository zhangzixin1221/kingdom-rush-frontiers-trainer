"""Extract printable string constants from LuaJIT bytecode files.

LuaJIT stores string constants inline as <uleb128 len><bytes>, so a simple
printable-run scan recovers the full constant table. Also reports which files
are plain Lua source rather than bytecode.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import re
import sys

SRC = _paths.EXTRACTED
OUT = _paths.DISASM
RUN = re.compile(rb"[\x20-\x7e]{3,}")

MAGIC = b"\x1bLJ"


def main():
    os.makedirs(OUT, exist_ok=True)
    agg = open(os.path.join(OUT, "ALL_STRINGS.txt"), "w", encoding="utf-8")
    bc = plain = 0
    plain_files = []
    for root, _d, files in os.walk(SRC):
        for fn in files:
            if not fn.endswith(".lua"):
                continue
            p = os.path.join(root, fn)
            rel = os.path.relpath(p, SRC).replace("\\", "/")
            with open(p, "rb") as f:
                data = f.read()
            if not data.startswith(MAGIC):
                plain += 1
                plain_files.append(rel)
                agg.write(f"\n===== FILE {rel} (PLAIN SOURCE) =====\n")
                agg.write(data.decode("utf-8", "replace"))
                continue
            bc += 1
            strs = [m.group().decode("utf-8", "replace") for m in RUN.finditer(data)]
            agg.write(f"\n===== FILE {rel} (bytecode, {len(data)} B, {len(strs)} strings) =====\n")
            agg.write("\n".join(strs))
            agg.write("\n")
    agg.close()
    print(f"bytecode files: {bc}, plain-source files: {plain}")
    if plain_files:
        print("plain source files:")
        for r in plain_files[:80]:
            print("  ", r)
    print("aggregate ->", os.path.join(OUT, "ALL_STRINGS.txt"),
          os.path.getsize(os.path.join(OUT, "ALL_STRINGS.txt")), "bytes")


main()
