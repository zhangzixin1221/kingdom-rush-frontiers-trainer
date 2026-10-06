"""Rebuild the fused LOVE exe, replacing selected archive entries.

A LOVE fused exe is: <small PE loader><zip>. The zip's stored offsets are
relative to the zip start. So we parse the original archive, re-emit every
entry (copying raw compressed bytes for untouched entries, so a 450 MB archive
is rebuilt in seconds), write a fresh central directory + EOCD, and prepend the
original PE prefix.

Usage:
    python repack.py --src <exe> --out <exe> [--replace name=localfile ...]
    python repack.py --src <exe> --list
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _paths  # noqa: E402
import argparse
import os
import struct
import sys
import zlib

EOCD_SIG = b"PK\x05\x06"
CD_SIG = b"PK\x01\x02"
LFH_SIG = b"PK\x03\x04"


class LoveArchive:
    def __init__(self, path):
        self.path = path
        self.f = open(path, "rb")
        self.size = os.path.getsize(path)
        self._parse_eocd()
        self._parse_cd()
        self._detect_shift()

    def close(self):
        self.f.close()

    def _parse_eocd(self):
        back = min(self.size, 70000)
        self.f.seek(self.size - back)
        buf = self.f.read(back)
        i = buf.rfind(EOCD_SIG)
        while i >= 0:
            if i + 22 <= len(buf):
                (sig, dnum, cdnum, n_this, n_total, cd_size, cd_off, clen) = \
                    struct.unpack("<IHHHHIIH", buf[i:i + 22])
                if i + 22 + clen == len(buf):
                    self.eocd_off = self.size - back + i
                    self.raw_cd_off = cd_off
                    self.cd_size = cd_size
                    self.n_total = n_total
                    self.comment = buf[i + 22:i + 22 + clen]
                    return
            i = buf.rfind(EOCD_SIG, 0, i)
        raise SystemExit("no EOCD found: not a fused LOVE archive?")

    def _parse_cd(self, shift=0):
        self.f.seek(self.raw_cd_off + shift)
        cd = self.f.read(self.cd_size)
        entries = []
        p = 0
        while p + 46 <= len(cd) and cd[p:p + 4] == CD_SIG:
            (sig, ver_made, ver_need, flags, method, mtime, mdate, crc, csize,
             usize, nlen, elen, clen, dstart, iattr, eattr, lho) = struct.unpack(
                "<IHHHHHHIIIHHHHHII", cd[p:p + 46])
            name = cd[p + 46:p + 46 + nlen]
            extra = cd[p + 46 + nlen:p + 46 + nlen + elen]
            comment = cd[p + 46 + nlen + elen:p + 46 + nlen + elen + clen]
            entries.append(dict(
                ver_made=ver_made, ver_need=ver_need, flags=flags, method=method,
                mtime=mtime, mdate=mdate, crc=crc, csize=csize, usize=usize,
                name=name, extra=extra, comment=comment, dstart=dstart,
                iattr=iattr, eattr=eattr, lho=lho))
            p += 46 + nlen + elen + clen
        return entries

    def _detect_shift(self):
        # The CD offset in the EOCD, and the local header offsets in the CD, may
        # be shifted relative to the real file positions. Try candidates.
        hint = self.eocd_off - self.cd_size - self.raw_cd_off
        cands = []
        for s in (hint, 0, 90112):
            if s not in cands and s >= 0:
                cands.append(s)
        best = None
        for s in cands:
            try:
                entries = self._parse_cd(s)
            except Exception:
                continue
            if len(entries) != self.n_total:
                continue
            ok = 0
            for e in entries[:150]:
                pos = e["lho"] + s
                if pos < 0 or pos + 30 > self.size:
                    continue
                self.f.seek(pos)
                head = self.f.read(30)
                if len(head) < 30 or head[:4] != LFH_SIG:
                    continue
                nlen = struct.unpack("<H", head[26:28])[0]
                if self.f.read(nlen) == e["name"]:
                    ok += 1
            if best is None or ok > best[1]:
                best = (s, ok, entries)
        if not best or best[1] == 0:
            raise SystemExit("could not locate local headers in archive")
        self.shift, self.entries = best[0], best[2]
        self.prefix_len = min(e["lho"] + self.shift for e in self.entries)

    def entry_by_name(self, name):
        nb = name.encode("utf-8")
        for e in self.entries:
            if e["name"] == nb:
                return e
        return None

    def raw_bytes(self, e):
        """Return the verbatim local-header + data block for an entry."""
        start = e["lho"] + self.shift
        self.f.seek(start)
        head = self.f.read(30)
        nlen, elen = struct.unpack("<HH", head[26:30])
        total = 30 + nlen + elen + e["csize"]
        self.f.seek(start)
        return self.f.read(total)

    def read_data(self, e):
        """Return the decompressed payload of an entry."""
        self.f.seek(e["lho"] + self.shift)
        head = self.f.read(30)
        nlen, elen = struct.unpack("<HH", head[26:30])
        self.f.seek(e["lho"] + self.shift + 30 + nlen + elen)
        body = self.f.read(e["csize"])
        if e["method"] == 8:
            return zlib.decompress(body, -15)
        if e["method"] == 0:
            return body
        raise SystemExit(f"unsupported method {e['method']} for {e['name']!r}")

    def rebuild(self, replacements, out_path, additions=None):
        """replacements/additions: {name(str): bytes}. Returns (size, report)."""
        additions = additions or {}
        with open(self.path, "rb") as src:
            src.seek(0)
            prefix = src.read(self.prefix_len)

        zipbuf = bytearray()
        new_cd = []
        report = []
        existing = {e["name"].decode("utf-8", "replace") for e in self.entries}
        tmpl = self.entries[0]
        for name, data in additions.items():
            if name in existing:
                raise SystemExit(f"addition {name} already exists; use replacements")
            crc = zlib.crc32(data) & 0xFFFFFFFF
            comp = zlib.compressobj(9, zlib.DEFLATED, -15)
            body = comp.compress(data) + comp.flush()
            if len(body) >= len(data):
                method, body = 0, data
            else:
                method = 8
            nb = name.encode("utf-8")
            off = len(zipbuf)
            zipbuf += struct.pack("<IHHHHHIIIHH", 0x04034b50, 20, 0, method,
                                  tmpl["mtime"], tmpl["mdate"], crc, len(body),
                                  len(data), len(nb), 0)
            zipbuf += nb + body
            new_cd.append(dict(ver_made=tmpl["ver_made"], ver_need=20, flags=0,
                               method=method, mtime=tmpl["mtime"],
                               mdate=tmpl["mdate"], crc=crc, csize=len(body),
                               usize=len(data), name=nb, extra=b"", comment=b"",
                               dstart=0, iattr=0, eattr=0, lho=off))
            report.append(f"  added    {name}: {len(data)} bytes")
        for e in self.entries:
            name = e["name"].decode("utf-8", "replace")
            if name in replacements:
                data = replacements[name]
                crc = zlib.crc32(data) & 0xFFFFFFFF
                comp = zlib.compressobj(9, zlib.DEFLATED, -15)
                body = comp.compress(data) + comp.flush()
                if len(body) >= len(data):
                    method, body, usize = 0, data, len(data)
                else:
                    method, usize = 8, len(data)
                flags = e["flags"] & ~0x08          # no data descriptor
                nlen = len(e["name"])
                off = len(zipbuf)
                lfh = struct.pack("<IHHHHHIIIHH", 0x04034b50, e["ver_need"] or 20,
                                  flags, method, e["mtime"], e["mdate"], crc,
                                  len(body), usize, nlen, len(e["extra"]))
                zipbuf += lfh + e["name"] + e["extra"] + body
                ne = dict(e)
                ne.update(crc=crc, csize=len(body), usize=usize, method=method,
                          flags=flags, lho=off)
                new_cd.append(ne)
                report.append(f"  replaced {name}: {e['usize']} -> {usize} bytes (method {method})")
            else:
                off = len(zipbuf)
                zipbuf += self.raw_bytes(e)
                ne = dict(e)
                ne["lho"] = off
                new_cd.append(ne)

        cd_off = len(zipbuf)
        for e in new_cd:
            zipbuf += struct.pack(
                "<IHHHHHHIIIHHHHHII", 0x02014b50, e["ver_made"], e["ver_need"],
                e["flags"], e["method"], e["mtime"], e["mdate"], e["crc"],
                e["csize"], e["usize"], len(e["name"]), len(e["extra"]),
                len(e["comment"]), e["dstart"], e["iattr"], e["eattr"], e["lho"])
            zipbuf += e["name"] + e["extra"] + e["comment"]
        cd_size = len(zipbuf) - cd_off
        zipbuf += struct.pack("<IHHHHIIH", 0x06054b50, 0, 0, len(new_cd),
                              len(new_cd), cd_size, cd_off, len(self.comment))
        zipbuf += self.comment

        with open(out_path, "wb") as out:
            out.write(prefix)
            out.write(zipbuf)
        return len(prefix) + len(zipbuf), report


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out")
    ap.add_argument("--replace", action="append", default=[])
    ap.add_argument("--list", action="store_true")
    a = ap.parse_args()

    ar = LoveArchive(a.src)
    print(f"prefix={ar.prefix_len} entries={len(ar.entries)} shift={ar.shift} "
          f"cd={ar.raw_cd_off}+{ar.cd_size} eocd={ar.eocd_off}")
    if a.list:
        for e in ar.entries:
            print(f"  {e['lho']:>10} {e['csize']:>10} {e['usize']:>10} "
                  f"{e['name'].decode('utf-8','replace')}")
        return
    reps = {}
    for spec in a.replace:
        name, _, path = spec.partition("=")
        with open(path, "rb") as fh:
            reps[name] = fh.read()
    if not reps:
        raise SystemExit("nothing to replace")
    if not a.out:
        raise SystemExit("--out required")
    n, report = ar.rebuild(reps, a.out)
    for line in report:
        print(line)
    print(f"wrote {a.out} ({n} bytes)")


if __name__ == "__main__":
    main()
