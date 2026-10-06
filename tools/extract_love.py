"""Extract Lua sources (and optionally everything) from the fused LOVE exe.

The exe is a PE image with a LOVE zip appended. The zip central directory is
located via EOCD; local-header offsets stored in the CD may be shifted, so we
detect the shift empirically by validating the PK\\x03\\x04 signature and the
stored file name at each candidate position.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import os
import struct
import sys
import zlib

EXE = _paths.game_exe()
OUT = _paths.EXTRACTED
WANT_ALL = "--all" in sys.argv


def find_eocd(f):
    f.seek(0, os.SEEK_END)
    size = f.tell()
    back = min(size, 70000)
    f.seek(size - back)
    buf = f.read(back)
    i = buf.rfind(b"PK\x05\x06")
    while i >= 0:
        if i + 22 <= len(buf):
            (sig, dnum, cdnum, n_this, n_total, cd_size, cd_off, clen) = struct.unpack(
                "<IHHHHIIH", buf[i:i + 22]
            )
            if i + 22 + clen == len(buf):
                return size - back + i, cd_off, cd_size, n_total
        i = buf.rfind(b"PK\x05\x06", 0, i)
    raise SystemExit("no EOCD found")


def read_cd(f, cd_off, cd_size, shift):
    f.seek(cd_off + shift)
    cd = f.read(cd_size)
    entries = []
    p = 0
    while p + 46 <= len(cd) and cd[p:p + 4] == b"PK\x01\x02":
        (_, vm, vn, flags, method, mtime, mdate, crc, csize, usize,
         nlen, elen, clen, dstart, iattr, eattr, lho) = struct.unpack(
            "<IHHHHHHIIIHHHHHII", cd[p:p + 46]
        )
        name = cd[p + 46:p + 46 + nlen].decode("utf-8", "replace")
        entries.append(dict(name=name, method=method, csize=csize, usize=usize,
                            crc=crc, lho=lho, flags=flags))
        p += 46 + nlen + elen + clen
    return entries


def detect_shift(f, entries, cd_off, cd_size, eocd_off):
    """Try candidate shifts and pick the one where local headers validate."""
    candidates = [0, eocd_off - cd_size - cd_off, 25600]
    best = None
    for shift in candidates:
        ok = 0
        for e in entries[:200]:
            pos = e["lho"] + shift
            if pos < 0:
                continue
            f.seek(pos)
            head = f.read(30)
            if len(head) < 30 or head[:4] != b"PK\x03\x04":
                continue
            nlen = struct.unpack("<H", head[26:28])[0]
            nm = f.read(nlen).decode("utf-8", "replace")
            if nm == e["name"]:
                ok += 1
        print(f"  shift={shift}: {ok}/200 local headers valid")
        if best is None or ok > best[1]:
            best = (shift, ok)
    return best[0]


def extract(f, e, shift, out_root):
    if e["name"].endswith("/"):
        return 0
    f.seek(e["lho"] + shift)
    head = f.read(30)
    if head[:4] != b"PK\x03\x04":
        return -1
    nlen, elen = struct.unpack("<HH", head[26:30])
    f.seek(e["lho"] + shift + 30 + nlen + elen)
    data = f.read(e["csize"])
    if e["method"] == 8:
        data = zlib.decompress(data, -15)
    elif e["method"] != 0:
        return -2
    dest = os.path.join(out_root, e["name"].replace("/", os.sep))
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "wb") as o:
        o.write(data)
    return len(data)


def main():
    with open(EXE, "rb") as f:
        eocd_off, cd_off, cd_size, n_total = find_eocd(f)
        print(f"EOCD @ {eocd_off}, CD @ {cd_off} size {cd_size}, entries {n_total}")
        # CD offset stored may also be shifted by the same amount
        shift_hint = eocd_off - cd_size - cd_off
        print(f"shift hint = {shift_hint}")
        for cand in (shift_hint, 0):
            try:
                entries = read_cd(f, cd_off, cd_size, cand)
                if len(entries) == n_total:
                    print(f"CD parsed with shift {cand}: {len(entries)} entries")
                    break
            except Exception as exc:
                print("  cd parse failed:", exc)
        else:
            raise SystemExit("could not parse CD")
        shift = detect_shift(f, entries, cd_off, cd_size, eocd_off)
        print(f"using shift = {shift}")
        total = 0
        count = 0
        fails = 0
        for e in entries:
            if not WANT_ALL and not e["name"].endswith(".lua"):
                continue
            r = extract(f, e, shift, OUT)
            if r < 0:
                fails += 1
            else:
                total += r
                count += 1
        print(f"extracted {count} files, {total/1024:.0f} KB, {fails} failures -> {OUT}")


main()
