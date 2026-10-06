"""Load all/components.lua against the REAL entity_db to recover component shapes.

loadmod.py stubs every require, so components register into a throwaway stub.
Here we load entity_db for real and route require("entity_db") to it.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lua_host import LuaHost  # noqa: E402

SRC = _paths.EXTRACTED.replace("\\", "/")
OUT = os.path.join(_paths.DISASM, "components.txt")

BOOT = r"""
local SRC = "__SRC__"
package.path = SRC.."/?.lua;"..SRC.."/?/init.lua;"..SRC.."/lib/?.lua;"
             ..SRC.."/lib/?/init.lua;"..SRC.."/all/?.lua;"..package.path

local function mkstub(name)
  local t = {}
  return setmetatable(t, {
    __index = function(_, k)
      if k == "__stub_name" then return name end
      return function(...) return t end
    end,
    __call = function(...) return t end,
    __tostring = function() return "<stub:"..name..">" end,
  })
end

love = love or {}
love.filesystem = love.filesystem or {}
love.filesystem.load = function(p)
  local f = loadfile(SRC.."/"..tostring(p):gsub("^/", ""))
  return f
end
love.filesystem.getRequirePath = function() return package.path end
love.filesystem.setRequirePath = function() end
love.filesystem.getSaveDirectory = function() return "." end
love.graphics = love.graphics or {}
love.window = love.window or {}
love.timer = love.timer or {}
love.math = love.math or {}
love.system = love.system or {}
love.audio = love.audio or {}
love.image = love.image or {}
love.event = love.event or {}

_G.log = _G.log or mkstub("log")

KR_PATH_ROOT = SRC
KR_PATH_GAME = "kr2"
KR_PATH_ALL = "all"
KR_PATH_GAME_TARGET = "kr2-desktop"
KR_PATH_ALL_TARGET = "all-desktop"
KR_PATH_ASSETS_ROOT = SRC.."/_assets"

-- 真实的 entity_db
local realdb
require = function(name)
  if name == "entity_db" then return realdb end
  return mkstub(tostring(name))
end
realdb = assert(loadfile(SRC.."/all/entity_db.lua"))()
pcall(function() realdb.load() end)
pcall(function() realdb:load() end)

-- constants 只负责设置全局，返回 nil
require = function(name)
  if name == "entity_db" then return realdb end
  return mkstub(tostring(name))
end
pcall(function() assert(loadfile(SRC.."/all/constants.lua"))() end)

-- 现在加载 components, 它会把自己注册进 realdb
local okc, errc = pcall(function() return assert(loadfile(SRC.."/all/components.lua"))() end)

function __dump(v, maxdepth)
  maxdepth = maxdepth or 3
  local seen = {}
  local function go(x, depth)
    local t = type(x)
    if t == "function" then return '"<fn>"' end
    if t ~= "table" then
      if t == "string" then return string.format("%q", x) end
      return tostring(x)
    end
    if seen[x] then return '"<cycle>"' end
    if depth > maxdepth then return '"<deep>"' end
    seen[x] = true
    local keys = {}
    for k in pairs(x) do keys[#keys+1] = k end
    table.sort(keys, function(a,b)
      local ta, tb = type(a), type(b)
      if ta ~= tb then return ta < tb end
      return tostring(a) < tostring(b)
    end)
    local parts = {}
    for _, k in ipairs(keys) do
      local ks = (type(k) == "string" and k:match("^[%a_][%w_]*$")) and k
                 or ("["..(type(k)=="string" and string.format("%q",k) or tostring(k)).."]")
      parts[#parts+1] = ks.." = "..go(x[k], depth+1)
    end
    seen[x] = nil
    return "{ "..table.concat(parts, ", ").." }"
  end
  return go(v, 0)
end

__CTX = { db = realdb, comp_ok = okc, comp_err = tostring(errc) }
""".replace("__SRC__", SRC)


def main():
    h = LuaHost()
    h.run(BOOT)
    expr = r"""
    local o = {}
    o[#o+1] = "components.lua load ok=" .. tostring(__CTX.comp_ok) ..
              " err=" .. tostring(__CTX.comp_err)
    local db = __CTX.db
    o[#o+1] = "entity_db keys: " .. __dump(db, 1)
    local comps = db.components or {}
    local names = {}
    for k in pairs(comps) do names[#names+1] = k end
    table.sort(names)
    o[#o+1] = "components: " .. table.concat(names, ", ")
    for _, name in ipairs(names) do
      o[#o+1] = "=== component[" .. name .. "] = " .. __dump(comps[name], 3)
    end
    return table.concat(o, "\n")
    """
    r = h.eval("local ok, s = pcall(function() " + expr + " end)\n"
               "if ok then return tostring(s) else return 'ERR: '..tostring(s) end")
    with open(OUT, "w", encoding="utf-8") as fh:
        fh.write(r)
    print(r[:6000])
    print("\n-> " + OUT)


main()
