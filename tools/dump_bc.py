"""Disassemble LuaJIT bytecode from the game with jit.bc, writing listings.

Usage: python tools/dump_bc.py <file.lua|dir> [more...]
Writes .txt listings next to a mirrored structure under OUTDIR.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lua_host import LuaHost  # noqa: E402

SRC = _paths.EXTRACTED
OUTDIR = _paths.DISASM

LUA_DUMP = r"""
local bc = require("jit.bc")
local path, outpath = ...
local f, err = loadfile(path)
if not f then return "LOADERR: " .. tostring(err) end
local fh, oerr = io.open(outpath, "w")
if not fh then return "IOERR: " .. tostring(oerr) end
local old = io.output(fh)
local ok, derr = pcall(bc.dump, f, true)
io.output(old)
fh:close()
if not ok then return "DUMPERR: " .. tostring(derr) end
return "OK"
"""


def main():
    h = LuaHost()
    # expose the runner as a Lua function taking (path, outpath)
    h.run("""
    function __dump(path, outpath)
      local bc = require("jit.bc")
      local f, err = loadfile(path)
      if not f then return "LOADERR: " .. tostring(err) end
      local fh, oerr = io.open(outpath, "w")
      if not fh then return "IOERR: " .. tostring(oerr) end
      local old = io.output(fh)
      local ok, derr = pcall(bc.dump, f, true)
      io.output(old)
      fh:close()
      if not ok then return "DUMPERR: " .. tostring(derr) end
      return "OK"
    end
    """)

    targets = []
    for a in sys.argv[1:]:
        if os.path.isdir(a):
            for root, _dirs, files in os.walk(a):
                for fn in files:
                    if fn.endswith(".lua"):
                        targets.append(os.path.join(root, fn))
        else:
            targets.append(a)
    if not targets:
        targets = [os.path.join(SRC, "kr2", "upgrades.lua")]

    ok = bad = 0
    for t in targets:
        rel = os.path.relpath(os.path.abspath(t), SRC).replace("\\", "/")
        out = os.path.join(OUTDIR, rel[:-4] + ".txt")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        r = h.eval("return __dump(%r, %r)" % (os.path.abspath(t).replace("\\", "/"), out.replace("\\", "/")))
        if r == "OK":
            ok += 1
            print(f"  ok   {rel}  ({os.path.getsize(out)} bytes)")
        else:
            bad += 1
            print(f"  FAIL {rel}: {r}")
    print(f"dumped {ok} ok, {bad} failed -> {OUTDIR}")


main()
