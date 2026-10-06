"""Minimal ctypes host for the game's bundled LuaJIT (lua51.dll).

Lets us run Lua in-process: load bytecode chunks, dump listings with jit.bc, etc.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import ctypes
import os
import sys

GAME = _paths.find_game_dir()
DLL = os.path.join(GAME, "lua51.dll")


class LuaHost:
    def __init__(self, dll=DLL):
        os.add_dll_directory(GAME)
        self.lib = ctypes.CDLL(dll)
        L = self.lib
        L.luaL_newstate.restype = ctypes.c_void_p
        L.luaL_openlibs.argtypes = [ctypes.c_void_p]
        L.luaL_loadstring.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        L.luaL_loadstring.restype = ctypes.c_int
        L.luaL_loadfile.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        L.luaL_loadfile.restype = ctypes.c_int
        L.lua_pcall.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int]
        L.lua_pcall.restype = ctypes.c_int
        L.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_size_t)]
        L.lua_tolstring.restype = ctypes.c_char_p
        L.lua_settop.argtypes = [ctypes.c_void_p, ctypes.c_int]
        L.lua_gettop.argtypes = [ctypes.c_void_p]
        L.lua_gettop.restype = ctypes.c_int
        L.lua_pushcclosure.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
        L.lua_createtable.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        L.lua_pushstring.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        L.lua_setfield.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p]
        L.lua_pushvalue.argtypes = [ctypes.c_void_p, ctypes.c_int]
        L.lua_rawseti.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        self.L = L
        self.state = L.luaL_newstate()
        if not self.state:
            raise RuntimeError("luaL_newstate failed")
        L.luaL_openlibs(self.state)

    def _err(self):
        L = self.L
        msg = L.lua_tolstring(self.state, -1, None)
        txt = msg.decode("utf-8", "replace") if msg else "<no message>"
        L.lua_settop(self.state, -2)
        return txt

    def run(self, code, chunk="=chunk"):
        L = self.L
        if L.luaL_loadstring(self.state, code.encode("utf-8")) != 0:
            raise RuntimeError("compile error: " + self._err())
        rc = L.lua_pcall(self.state, 0, 0, 0)
        if rc != 0:
            raise RuntimeError("runtime error: " + self._err())

    def version(self):
        self.run('_G.__v = tostring(_VERSION) .. " | " .. (jit and jit.version or "no-jit")')
        return self.eval('return _G.__v')

    def eval(self, code):
        """Run `code` which must return a single string; return it."""
        L = self.L
        if L.luaL_loadstring(self.state, code.encode("utf-8")) != 0:
            raise RuntimeError("compile error: " + self._err())
        rc = L.lua_pcall(self.state, 0, 1, 0)
        if rc != 0:
            raise RuntimeError("runtime error: " + self._err())
        n = ctypes.c_size_t()
        p = L.lua_tolstring(self.state, -1, ctypes.byref(n))
        s = p.decode("utf-8", "replace") if p else ""
        L.lua_settop(self.state, -2)
        return s


def main():
    h = LuaHost()
    print("host ok, state =", hex(h.state))
    print("version:", h.version())
    if len(sys.argv) > 1:
        path = sys.argv[1]
        src = open(path, "r", encoding="utf-8").read()
        print(h.eval(src))


if __name__ == "__main__":
    main()
