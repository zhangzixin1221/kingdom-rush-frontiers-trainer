"""Probe jit.util API shape on a real game bytecode file."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lua_host import LuaHost  # noqa: E402

PKG = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET = sys.argv[1] if len(sys.argv) > 1 else os.path.join(PKG, "src", "kr2", "upgrades.lua")
TARGET = os.path.abspath(TARGET).replace("\\", "/")

LUA = """
local util = require("jit.util")
local path = ...
local buf = {}
local function w(...) local t = {...}; for i=1,#t do buf[#buf+1] = tostring(t[i]) end end
local f, err = loadfile(path)
w("loadfile -> ", tostring(f), " err=", tostring(err), "\\n")
if not f then return table.concat(buf) end
local info = util.funcinfo(f)
for k, v in pairs(info) do w("info.", k, " = ", tostring(v), "\\n") end
w("--- constants (first 25) ---\\n")
for i = 1, 25 do
  local ok, v, t = pcall(util.funck, f, i)
  if not ok then w("K", i, " ERR ", tostring(v), "\\n") break end
  if v == nil then w("K", i, " -> end\\n") break end
  w("K", i, " type=", tostring(t), " lua_type=", type(v), " value=", tostring(v), "\\n")
end
w("--- bytecode (first 12) ---\\n")
for i = 1, 12 do
  local ok, bc, op = pcall(util.funcbc, f, i)
  if not ok then w("B", i, " ERR ", tostring(bc), "\\n") break end
  if bc == nil then w("B", i, " -> end\\n") break end
  w("B", i, " op=", tostring(op), " bc=", tostring(bc), "\\n")
end
w("--- upvalues ---\\n")
for i = 1, 6 do
  local ok, nm = pcall(util.funcuvname, f, i)
  if not ok or nm == nil then break end
  w("U", i, " ", tostring(nm), "\\n")
end
return table.concat(buf)
"""


def main():
    h = LuaHost()
    h.run("function __probe(path) " + LUA.replace("local path = ...", "local path = path") + " end")
    print(h.eval("return __probe(%r)" % TARGET))


main()
