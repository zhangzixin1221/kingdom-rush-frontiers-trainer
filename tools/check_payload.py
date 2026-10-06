"""Syntax-check src/payload.lua with the game's own LuaJIT before installing it.

A syntax error in the payload means the game boots into an error screen, so it is
worth catching here rather than in the game.

Usage: python tools/check_payload.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
from lua_host import LuaHost  # noqa: E402

PAYLOAD = os.path.join(_paths.ROOT, "src", "payload.lua")


def main():
    src = open(PAYLOAD, encoding="utf-8").read()
    for ph, val in (("__CHEAT_LOG__", "C:/tmp/kr.log"),
                    ("__CHEAT_CTL__", "C:/tmp/kr_ctl.txt"),
                    ("__CHEAT_CMD__", "C:/tmp/kr_cmd.txt")):
        src = src.replace(ph, val)
    if "__CHEAT" in src:
        print("警告: 还有未替换的占位符")

    h = LuaHost()
    h.run("__SRC = [==[\n" + src + "\n]==]")
    r = h.eval(
        'local f, e = loadstring(__SRC)\n'
        'if f then return "SYNTAX OK" else return "SYNTAX ERROR: " .. tostring(e) end'
    )
    print(r)
    print("文件:", PAYLOAD, "大小:", len(src), "字节, 行数:", src.count("\n") + 1)
    return 0 if r == "SYNTAX OK" else 1


sys.exit(main())
