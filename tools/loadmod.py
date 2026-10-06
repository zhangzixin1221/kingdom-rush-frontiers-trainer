"""Load a game bytecode module in the bundled LuaJIT 2.0.4 with stubbed deps.

Usage:
  python tools/loadmod.py <path-relative-to-src> [expr-file-or-inline-lua]

Writes the expression result to disasm/modout.txt (utf-8) and prints the head.
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
HERE = os.path.dirname(os.path.abspath(__file__))

BOOT = r"""
local SRC = "__SRC__"
package.path = SRC.."/?.lua;"..SRC.."/?/init.lua;"..SRC.."/lib/?.lua;"
             ..SRC.."/lib/?/init.lua;"..SRC.."/all/?.lua;"..package.path

__STUB_CALLS = {}
local function mkstub(name)
  local t = {}
  local mt
  mt = {
    __index = function(_, k)
      if k == "__stub_name" then return name end
      return function(...) return t end
    end,
    __call = function(...) return t end,
    __tostring = function() return "<stub:" .. name .. ">" end,
  }
  return setmetatable(t, mt)
end

require = function(name)
  __STUB_CALLS[#__STUB_CALLS+1] = name
  return mkstub(name)
end

-- seed only the logger, which the real boot always defines first
for _, g in ipairs({"log"}) do
  if _G[g] == nil then _G[g] = mkstub(g) end
end

-- minimal love shim so filesystem-dependent modules can load real assets
local function cands(p)
  p = tostring(p):gsub("^/", "")
  return { SRC .. "/" .. p, SRC .. "/kr2/" .. p, SRC .. "/all/" .. p, SRC .. "/" .. p }
end
love = love or {}
love.filesystem = love.filesystem or {}
function love.filesystem.load(p)
  for _, c in ipairs(cands(p)) do
    local f = loadfile(c)
    if f then return f end
  end
  return nil, "not found: " .. tostring(p)
end
function love.filesystem.read(p)
  for _, c in ipairs(cands(p)) do
    local fh = io.open(c, "rb")
    if fh then local s = fh:read("*a"); fh:close(); return s end
  end
  return nil, "not found"
end
function love.filesystem.exists(p)
  for _, c in ipairs(cands(p)) do
    local fh = io.open(c, "rb")
    if fh then fh:close(); return true end
  end
  return false
end
love.filesystem.isFile = love.filesystem.exists
love.filesystem.isDirectory = function() return false end
love.filesystem.getDirectoryItems = function() return {} end
love.filesystem.getSaveDirectory = function() return "." end
love.filesystem.getUserDirectory = function() return "." end
love.filesystem.getSourceBaseDirectory = function() return SRC end
love.filesystem.getWorkingDirectory = function() return SRC end
love.filesystem.getRealDirectory = function() return SRC end
love.filesystem.setIdentity = function() end
love.filesystem.mount = function() return true end
love.graphics = love.graphics or {}
love.window = love.window or {}
love.timer = love.timer or {}
love.math = love.math or {}
love.system = love.system or {}
love.audio = love.audio or {}
love.image = love.image or {}
love.event = love.event or {}
love.filesystem.getRequirePath = function() return package.path end
love.filesystem.setRequirePath = function() end


-- path globals normally created by main.lua
KR_PATH_ROOT = SRC
KR_FULLPATH_BASE = SRC
KR_PATH_GAME = "kr2"
KR_PATH_ALL = "all"
KR_PATH_GAME_TARGET = "kr2-desktop"
KR_PATH_ALL_TARGET = "all-desktop"
KR_PATH_ASSETS_ROOT = SRC .. "/_assets"
KR_PATH_ASSETS_GAME_TARGET = SRC .. "/_assets/kr2-desktop"
KR_PATH_ASSETS_ALL_TARGET = SRC .. "/_assets"
KR_PATH_ASSETS_GAME_FALLBACK = SRC .. "/_assets/kr2-desktop"
KR_PATH_ASSETS_ALL_FALLBACK = SRC .. "/_assets"

__SNAP = {}
for k in pairs(_G) do __SNAP[k] = true end
function __newglobals(maxd)
  local n = {}
  for k, v in pairs(_G) do
    if not __SNAP[k] then
      n[#n+1] = k .. " (" .. type(v) .. ")"
      if type(v) == "table" and maxd then n[#n+1] = "   = " .. __dump(v, maxd) end
    end
  end
  table.sort(n)
  return table.concat(n, "\n")
end

function __load(p)
  local f, err = loadfile(p)
  if not f then return "LOADERR: "..tostring(err) end
  local ok, res = pcall(f)
  if not ok then return "RUNERR: "..tostring(res) end
  return res
end

-- dumper: collapses functions/userdata, tracks cycles
function __dump(v, maxdepth)
  maxdepth = maxdepth or 14
  local seen = {}
  local function go(x, depth)
    local t = type(x)
    if t == "function" then return '"<function>"' end
    if t ~= "table" then
      if t == "string" then return string.format("%q", x) end
      return tostring(x)
    end
    if seen[x] then return '"<cycle>"' end
    if depth > maxdepth then return '"<deep>"' end
    seen[x] = true
    local keys = {}
    for k in pairs(x) do keys[#keys+1] = k end
    table.sort(keys, function(a, b)
      local ta, tb = type(a), type(b)
      if ta ~= tb then return ta < tb end
      if ta == "number" or ta == "string" then return a < b end
      return tostring(a) < tostring(b)
    end)
    local parts = {}
    for _, k in ipairs(keys) do
      local ks
      if type(k) == "string" and k:match("^[%a_][%w_]*$") then ks = k
      else ks = "[" .. (type(k) == "string" and string.format("%q", k) or tostring(k)) .. "]" end
      parts[#parts+1] = ks .. " = " .. go(x[k], depth + 1)
    end
    seen[x] = nil
    return "{ " .. table.concat(parts, ", ") .. " }"
  end
  return go(v, 0)
end

function __upvals(f)
  local out = {}
  local i = 1
  while true do
    local n, v = debug.getupvalue(f, i)
    if not n then break end
    out[#out+1] = n .. " = " .. (type(v) == "table" and __dump(v) or tostring(v))
    i = i + 1
  end
  return "{" .. table.concat(out, " ;; ") .. "}"
end
""".replace("__SRC__", SRC)


def out(s):
    sys.stdout.buffer.write((s + "\n").encode("utf-8", "replace"))
    sys.stdout.buffer.flush()


def main():
    rel = sys.argv[1]
    expr = sys.argv[2] if len(sys.argv) > 2 else "return __dump(M)"
    if os.path.isfile(expr):
        expr = open(expr, encoding="utf-8").read()

    # a comma-separated list loads modules in order; M = last non-nil result
    rels = [r.strip() for r in rel.split(",") if r.strip()]
    lua_calls = []
    for i, r in enumerate(rels):
        p = SRC + "/" + r.replace("\\", "/")
        lua_calls.append("  r = __load(%r); print('  [' .. %d .. '] %s -> ' .. tostring(r))"
                         % (p, i, r.replace("\\", "\\\\")))
    load_code = "local r\n" + "\n".join(lua_calls) + "\n  _G.M = r"

    h = LuaHost()
    h.run(BOOT)
    out("LOAD:")
    h.run(load_code)
    out("STUBS: " + h.eval("return table.concat(__STUB_CALLS, ', ')"))
    try:
        r = h.eval("local ok, s = pcall(function() " + expr + " end)\n"
                   "if ok then return tostring(s) else return 'EXPRERR: '..tostring(s) end")
        outpath = os.path.join(HERE, "..", "disasm", "modout.txt")
        with open(outpath, "w", encoding="utf-8") as fh:
            fh.write(r)
        out("RESULT -> %s (%d chars)" % (os.path.abspath(outpath), len(r)))
        out("---- head ----")
        out(r[:3000])
    except RuntimeError as e:
        out("EXPR FAILED: " + str(e))


if __name__ == "__main__":
    main()
