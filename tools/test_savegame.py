"""Round-trip test for the save parser/serializer against the real save."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import sys
import difflib

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "legacy"))
import savegame  # noqa: E402

PATH = os.path.join(_paths.appdata_save_dir(), "slot_1.lua")

orig = open(PATH, encoding="utf-8", errors="surrogateescape").read()
table = savegame.loads(orig)
print("parsed top-level keys:", sorted(table.keys()))
print("gems       =", table.get("gems"))
print("difficulty =", table.get("difficulty"))
print("upgrades   =", table.get("upgrades"))
print("bag        =", table.get("bag"))
print("levels     =", {k: table["levels"][k] for k in sorted(table["levels"])})
print("heroes     =", sorted(table["heroes"]["status"].keys()))
print("total_stars=", savegame.total_stars(table))

again = savegame.dumps(table)
print("\n--- round-trip diff (empty == identical) ---")
diff = list(difflib.unified_diff(orig.splitlines(), again.splitlines(),
                                 "original", "reserialized", lineterm=""))
for line in diff[:40]:
    print(line)
print(f"(diff lines: {len(diff)})")

# exercise the operations on a copy
import copy  # noqa: E402
work = copy.deepcopy(table)
for line in savegame.apply_all(work):
    print("  ", line)
print("after: gems =", work["gems"], "total_stars =", savegame.total_stars(work))
print("after: upgrades =", work["upgrades"])
alric = work["heroes"]["status"].get("hero_alric")
print("after: hero_alric =", alric)
print("schema same?", sorted(work.keys()) == sorted(table.keys()))
