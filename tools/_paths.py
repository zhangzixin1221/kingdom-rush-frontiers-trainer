"""Shared paths for the toolchain.

Everything is resolved relative to this repository so the tools work on any
machine, and the Steam install is located via the registry + libraryfolders.vdf
instead of a hard-coded drive letter.
"""
from __future__ import annotations

import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# 从游戏 exe 里提取出来的 Lua 字节码（分析用临时目录，不入库）
EXTRACTED = os.path.join(ROOT, "extracted")
# 分析产物：字符串常量、反汇编、探针输出（不入库）
DISASM = os.path.join(ROOT, "disasm")

GAME_DIR_NAME = "Kingdom Rush Frontiers"
EXE_NAME = "Kingdom Rush Frontiers.exe"


def _steam_roots():
    roots = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = winreg.QueryValueEx(k, "SteamPath")[0]
        steam = steam.replace("/", "\\")
        roots.append(steam)
        vdf = os.path.join(steam, "steamapps", "libraryfolders.vdf")
        if os.path.isfile(vdf):
            with open(vdf, encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    if '"path"' not in line:
                        continue
                    parts = line.split('"path"')[1].split('"')
                    if len(parts) >= 2:
                        roots.append(parts[1].replace("\\\\", "\\"))
    except Exception:                                              # noqa: BLE001
        pass
    return roots


def find_game_dir():
    """Return the game install directory, or None."""
    for root in _steam_roots():
        cand = os.path.join(root, "steamapps", "common", GAME_DIR_NAME)
        if os.path.isfile(os.path.join(cand, EXE_NAME)):
            return cand
    return None


def game_exe():
    d = find_game_dir()
    if d is None:
        raise SystemExit("找不到游戏安装目录，请确认已安装 Steam 版《王国保卫战：前线》。")
    return os.path.join(d, EXE_NAME)


def appdata_save_dir():
    """The game's save directory (%APPDATA%\\kingdom_rush_frontiers)."""
    appdata = os.environ.get("APPDATA", "")
    for name in ("kingdom_rush_frontiers", "Kingdom Rush Frontiers"):
        d = os.path.join(appdata, name)
        if os.path.isdir(d):
            return d
    return os.path.join(appdata, "kingdom_rush_frontiers")
