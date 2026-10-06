"""Build a game copy whose main.lua is replaced by an injected Lua script.

The original main.lua bytecode is preserved as main_orig.lua so the injected
script can run it and then install hooks.

Usage: python tools/build_injected.py <injected_main.lua> [outdir]
Default outdir: <pkg>/testgame
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from repack import LoveArchive  # noqa: E402

PKG = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = _paths.find_game_dir()
EXE_NAME = "Kingdom Rush Frontiers.exe"


def build(injected_lua, outdir):
    src_exe = os.path.join(GAME, EXE_NAME)
    os.makedirs(outdir, exist_ok=True)
    with open(injected_lua, "rb") as fh:
        payload = fh.read()

    ar = LoveArchive(src_exe)
    e = ar.entry_by_name("main.lua")
    if e is None:
        raise SystemExit("main.lua not found in archive")
    orig_main = ar.read_data(e)
    print(f"archive prefix={ar.prefix_len} entries={len(ar.entries)}; "
          f"original main.lua {len(orig_main)} B magic={orig_main[:3]!r}")

    out_exe = os.path.join(outdir, EXE_NAME)
    n, report = ar.rebuild(
        replacements={"main.lua": payload},
        additions={"main_orig.lua": orig_main},
        out_path=out_exe,
    )
    for line in report:
        print(line)
    ar.close()

    for fn in os.listdir(GAME):
        s = os.path.join(GAME, fn)
        if not os.path.isfile(s) or os.path.abspath(s) == os.path.abspath(src_exe):
            continue
        shutil.copy2(s, os.path.join(outdir, fn))
    print(f"built {out_exe} ({n} bytes) in {outdir}")
    return out_exe


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    img = sys.argv[1]
    od = sys.argv[2] if len(sys.argv) > 2 else os.path.join(PKG, "testgame")
    build(img, od)
