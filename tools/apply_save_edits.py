"""Apply the full unlock set to the live save slot (with an automatic backup)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import sys

PKG = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(PKG, "legacy"))
import savegame  # noqa: E402

PATH = os.path.join(_paths.appdata_save_dir(), "slot_1.lua")


def main():
    table = savegame.load_slot(PATH)
    print("before: gems=%s stars=%s upgrades=%s bag=%s" % (
        table.get("gems"), savegame.total_stars(table),
        table.get("upgrades"), table.get("bag")))
    bak = savegame.backup(PATH)
    print("backup ->", os.path.basename(bak))
    for line in savegame.apply_all(table):
        print("  ", line)
    savegame.save_slot(PATH, table)

    check = savegame.load_slot(PATH)
    alric = (check["heroes"]["status"] or {}).get("hero_alric") or {}
    print("after : gems=%s stars=%s upgrades=%s bag=%s" % (
        check.get("gems"), savegame.total_stars(check),
        check.get("upgrades"), check.get("bag")))
    print("after : hero_alric xp=%s skills=%s" % (alric.get("xp"), alric.get("skills")))
    print("reparse OK, schema keys =", sorted(check.keys()))


main()
