// KrTrainer.exe -- 王国保卫战：前线 修改器
//
// 编译: src\build.bat   (调用系统自带 csc.exe, 需要 .NET Framework 4.x)
//
// 架构:
//   本 exe 是功能开关的唯一来源, 把开关写进 %APPDATA%\krft_trainer\control.txt;
//   游戏内注入的 Lua 每 0.5 秒读一次, 因此界面 / 热键 / 游戏内效果永远一致。
//   游戏本体需要先被"激活"(重打包 exe 注入 main.lua), 原文件会自动备份。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KrTrainer
{
    // =============================================================== 主题 ====
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(22, 25, 31);
        public static readonly Color Card = Color.FromArgb(32, 36, 44);
        public static readonly Color CardHover = Color.FromArgb(40, 45, 55);
        public static readonly Color Border = Color.FromArgb(52, 59, 72);
        public static readonly Color Text = Color.FromArgb(233, 237, 245);
        public static readonly Color Muted = Color.FromArgb(137, 147, 165);
        public static readonly Color Green = Color.FromArgb(48, 196, 122);
        public static readonly Color Blue = Color.FromArgb(72, 132, 235);
        public static readonly Color Slate = Color.FromArgb(58, 66, 80);
        public static readonly Color HeaderTop = Color.FromArgb(30, 62, 100);
        public static readonly Color HeaderBottom = Color.FromArgb(20, 25, 34);

        public static Font F(float size) { return new Font("Microsoft YaHei UI", size); }
        public static Font FB(float size) { return new Font("Microsoft YaHei UI", size, FontStyle.Bold); }
    }

    // ========================================================== 尺寸缩放 ====
    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    // ======================================================== 配置与开关 ====
    static class Ctl
    {
        public static string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "krft_trainer");
        public static string File_ = Path.Combine(Dir, "control.txt");
        public static string CmdFile = Path.Combine(Dir, "command.txt");
        public static string SetFile = Path.Combine(Dir, "settings.txt");
        public static string HotkeyFile = Path.Combine(Dir, "hotkeys.txt");

        // 顺序必须和界面功能列表一致
        public static readonly string[] Keys = {
            "gold", "lives", "kill", "ally", "heal", "weak",
            "slow", "freeze", "cd", "score", "xpmul", "towers"
        };

        /// <summary>一次性指令: 游戏读到就执行, 然后清空文件。</summary>
        public static void SendCommand(string cmd)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(CmdFile, cmd + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Write(bool[] v)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Keys.Length; i++)
                    sb.Append(Keys[i]).Append('=').Append(i < v.Length && v[i] ? 1 : 0).Append('\n');
                File.WriteAllText(File_, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static bool[] Read()
        {
            bool[] v = new bool[Keys.Length];
            v[0] = true; v[1] = true;
            try
            {
                if (!File.Exists(File_)) return v;
                foreach (string line in File.ReadAllLines(File_))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    for (int i = 0; i < Keys.Length; i++)
                        if (Keys[i] == k) v[i] = (val == "1");
                }
            }
            catch { }
            return v;
        }

        public static Dictionary<string, string> LoadMap(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(path)) return d;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        public static void SaveMap(string path, Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in d)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ============================================================== CRC32 ====
    static class Crc32
    {
        static readonly uint[] Table = Build();
        static uint[] Build()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }
        public static uint Compute(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

    // ====================================================== LÖVE 归档读写 ====
    class ZipEntry
    {
        public byte[] Name;
        public string NameStr;
        public int VerMade, VerNeed, Flags, Method, MTime, MDate;
        public uint Crc;
        public long CSize, USize;
        public int DStart, IAttr;
        public uint EAttr;
        public long Lho;
        public byte[] Extra = new byte[0];
        public byte[] Comment = new byte[0];
    }

    class LoveArchive : IDisposable
    {
        public FileStream Src;
        public List<ZipEntry> Entries = new List<ZipEntry>();
        public long PrefixLen, Shift, EocdOff, CdOffRaw, CdSize;
        public long Size;

        public LoveArchive(string path)
        {
            Src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            Size = Src.Length;
            ParseEocd();
            DetectShift();
        }

        public void Dispose() { if (Src != null) Src.Close(); }

        void ParseEocd()
        {
            int back = (int)Math.Min(Size, 70000);
            byte[] buf = new byte[back];
            Src.Position = Size - back;
            ReadFull(buf, 0, back);
            for (int i = back - 22; i >= 0; i--)
            {
                if (buf[i] != 0x50 || buf[i + 1] != 0x4B || buf[i + 2] != 0x05 || buf[i + 3] != 0x06)
                    continue;
                int clen = buf[i + 20] | (buf[i + 21] << 8);
                if (i + 22 + clen != back) continue;
                CdSize = BitConverter.ToUInt32(buf, i + 12);
                CdOffRaw = BitConverter.ToUInt32(buf, i + 16);
                EocdOff = Size - back + i;
                return;
            }
            throw new Exception("找不到 ZIP 结尾记录：这不是 LÖVE 打包的 exe？");
        }

        List<ZipEntry> ParseCd(long shift)
        {
            Src.Position = CdOffRaw + shift;
            byte[] cd = new byte[CdSize];
            ReadFull(cd, 0, (int)CdSize);
            List<ZipEntry> list = new List<ZipEntry>();
            int p = 0;
            while (p + 46 <= cd.Length &&
                   cd[p] == 0x50 && cd[p + 1] == 0x4B && cd[p + 2] == 0x01 && cd[p + 3] == 0x02)
            {
                ZipEntry e = new ZipEntry();
                e.VerMade = BitConverter.ToUInt16(cd, p + 4);
                e.VerNeed = BitConverter.ToUInt16(cd, p + 6);
                e.Flags = BitConverter.ToUInt16(cd, p + 8);
                e.Method = BitConverter.ToUInt16(cd, p + 10);
                e.MTime = BitConverter.ToUInt16(cd, p + 12);
                e.MDate = BitConverter.ToUInt16(cd, p + 14);
                e.Crc = BitConverter.ToUInt32(cd, p + 16);
                e.CSize = BitConverter.ToUInt32(cd, p + 20);
                e.USize = BitConverter.ToUInt32(cd, p + 24);
                int nlen = BitConverter.ToUInt16(cd, p + 28);
                int elen = BitConverter.ToUInt16(cd, p + 30);
                int clen = BitConverter.ToUInt16(cd, p + 32);
                e.DStart = BitConverter.ToUInt16(cd, p + 34);
                e.IAttr = BitConverter.ToUInt16(cd, p + 36);
                e.EAttr = BitConverter.ToUInt32(cd, p + 38);
                e.Lho = BitConverter.ToUInt32(cd, p + 42);
                e.Name = new byte[nlen];
                Array.Copy(cd, p + 46, e.Name, 0, nlen);
                e.NameStr = Encoding.UTF8.GetString(e.Name);
                e.Extra = new byte[elen];
                Array.Copy(cd, p + 46 + nlen, e.Extra, 0, elen);
                e.Comment = new byte[clen];
                Array.Copy(cd, p + 46 + nlen + elen, e.Comment, 0, clen);
                list.Add(e);
                p += 46 + nlen + elen + clen;
            }
            return list;
        }

        void DetectShift()
        {
            long hint = EocdOff - CdSize - CdOffRaw;
            long[] cands = new long[] { hint, 0, 90112 };
            List<ZipEntry> best = null;
            int bestOk = -1;
            long bestShift = 0;
            for (int ci = 0; ci < cands.Length; ci++)
            {
                long s = cands[ci];
                if (s < 0) continue;
                bool dup = false;
                for (int j = 0; j < ci; j++) if (cands[j] == s) dup = true;
                if (dup) continue;
                List<ZipEntry> list;
                try { list = ParseCd(s); } catch { continue; }
                if (list.Count == 0) continue;
                int ok = 0, tried = 0;
                for (int i = 0; i < list.Count && tried < 150; i++, tried++)
                {
                    long pos = list[i].Lho + s;
                    if (pos < 0 || pos + 30 > Size) continue;
                    Src.Position = pos;
                    byte[] h = new byte[30];
                    ReadFull(h, 0, 30);
                    if (h[0] != 0x50 || h[1] != 0x4B || h[2] != 0x03 || h[3] != 0x04) continue;
                    int nlen = h[26] | (h[27] << 8);
                    byte[] nm = new byte[nlen];
                    ReadFull(nm, 0, nlen);
                    bool same = nm.Length == list[i].Name.Length;
                    if (same) for (int k = 0; k < nlen; k++) if (nm[k] != list[i].Name[k]) { same = false; break; }
                    if (same) ok++;
                }
                if (ok > bestOk) { bestOk = ok; best = list; bestShift = s; }
            }
            if (best == null || bestOk == 0)
                throw new Exception("无法定位归档内的文件头，游戏版本可能不兼容。");
            Entries = best;
            Shift = bestShift;
            PrefixLen = long.MaxValue;
            foreach (ZipEntry e in Entries)
            {
                long p = e.Lho + Shift;
                if (p < PrefixLen) PrefixLen = p;
            }
        }

        void ReadFull(byte[] buf, int off, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = Src.Read(buf, off + done, count - done);
                if (n <= 0) throw new EndOfStreamException();
                done += n;
            }
        }

        public ZipEntry Find(string name)
        {
            foreach (ZipEntry e in Entries) if (e.NameStr == name) return e;
            return null;
        }

        public byte[] ReadData(ZipEntry e)
        {
            Src.Position = e.Lho + Shift;
            byte[] h = new byte[30];
            ReadFull(h, 0, 30);
            int nlen = h[26] | (h[27] << 8);
            int elen = h[28] | (h[29] << 8);
            Src.Position = e.Lho + Shift + 30 + nlen + elen;
            byte[] body = new byte[e.CSize];
            ReadFull(body, 0, (int)e.CSize);
            if (e.Method == 0) return body;
            if (e.Method == 8)
            {
                using (MemoryStream ms = new MemoryStream(body))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream outp = new MemoryStream())
                {
                    byte[] tmp = new byte[65536];
                    int n;
                    while ((n = ds.Read(tmp, 0, tmp.Length)) > 0) outp.Write(tmp, 0, n);
                    return outp.ToArray();
                }
            }
            throw new Exception("不支持的压缩方式: " + e.Method);
        }

        static byte[] Deflate(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public long Rebuild(Dictionary<string, byte[]> replacements,
                            Dictionary<string, byte[]> additions,
                            string outPath)
        {
            Dictionary<string, ZipEntry> byName = new Dictionary<string, ZipEntry>();
            foreach (ZipEntry e in Entries) byName[e.NameStr] = e;

            List<ZipEntry> cd = new List<ZipEntry>();
            using (FileStream outp = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 1 << 20))
            {
                Src.Position = 0;
                CopyBytes(Src, outp, PrefixLen);

                byte[] chunk = new byte[1 << 20];
                foreach (ZipEntry e in Entries)
                {
                    long newOff = outp.Position;
                    if (replacements.ContainsKey(e.NameStr))
                    {
                        byte[] data = replacements[e.NameStr];
                        byte[] body = Deflate(data);
                        int method = 8;
                        if (body.Length >= data.Length) { body = data; method = 0; }
                        uint crc = Crc32.Compute(data);
                        int flags = e.Flags & ~0x08;
                        WriteLocal(outp, e.VerNeed, flags, method, e.MTime, e.MDate, crc,
                                   body.Length, data.Length, e.Name, e.Extra, body);
                        ZipEntry ne = Clone(e);
                        ne.Crc = crc; ne.CSize = body.Length; ne.USize = data.Length;
                        ne.Method = method; ne.Flags = flags; ne.Lho = newOff;
                        cd.Add(ne);
                    }
                    else
                    {
                        Src.Position = e.Lho + Shift;
                        byte[] h = new byte[30];
                        ReadFull(h, 0, 30);
                        int nlen = h[26] | (h[27] << 8);
                        int elen = h[28] | (h[29] << 8);
                        long total = 30L + nlen + elen + e.CSize;
                        Src.Position = e.Lho + Shift;
                        CopyBytes(Src, outp, total, chunk);
                        ZipEntry ne = Clone(e);
                        ne.Lho = newOff;
                        cd.Add(ne);
                    }
                }

                ZipEntry tmpl = Entries.Count > 0 ? Entries[0] : new ZipEntry();
                foreach (KeyValuePair<string, byte[]> kv in additions)
                {
                    if (byName.ContainsKey(kv.Key))
                        throw new Exception("要新增的文件已存在: " + kv.Key);
                    byte[] data = kv.Value;
                    byte[] body = Deflate(data);
                    int method = 8;
                    if (body.Length >= data.Length) { body = data; method = 0; }
                    uint crc = Crc32.Compute(data);
                    byte[] nm = Encoding.UTF8.GetBytes(kv.Key);
                    long off = outp.Position;
                    WriteLocal(outp, 20, 0, method, tmpl.MTime, tmpl.MDate, crc,
                               body.Length, data.Length, nm, new byte[0], body);
                    ZipEntry ne = new ZipEntry();
                    ne.Name = nm; ne.NameStr = kv.Key;
                    ne.VerMade = tmpl.VerMade; ne.VerNeed = 20;
                    ne.Method = method; ne.MTime = tmpl.MTime; ne.MDate = tmpl.MDate;
                    ne.Crc = crc; ne.CSize = body.Length; ne.USize = data.Length;
                    ne.Lho = off;
                    cd.Add(ne);
                }

                long cdOff = outp.Position;
                foreach (ZipEntry e in cd)
                {
                    byte[] hdr = new byte[46];
                    PutU32(hdr, 0, 0x02014b50);
                    PutU16(hdr, 4, e.VerMade); PutU16(hdr, 6, e.VerNeed);
                    PutU16(hdr, 8, e.Flags); PutU16(hdr, 10, e.Method);
                    PutU16(hdr, 12, e.MTime); PutU16(hdr, 14, e.MDate);
                    PutU32(hdr, 16, e.Crc);
                    PutU32(hdr, 20, (uint)e.CSize); PutU32(hdr, 24, (uint)e.USize);
                    PutU16(hdr, 28, e.Name.Length); PutU16(hdr, 30, e.Extra.Length);
                    PutU16(hdr, 32, e.Comment.Length);
                    PutU16(hdr, 34, e.DStart); PutU16(hdr, 36, e.IAttr);
                    PutU32(hdr, 38, e.EAttr); PutU32(hdr, 42, (uint)e.Lho);
                    outp.Write(hdr, 0, 46);
                    outp.Write(e.Name, 0, e.Name.Length);
                    if (e.Extra.Length > 0) outp.Write(e.Extra, 0, e.Extra.Length);
                    if (e.Comment.Length > 0) outp.Write(e.Comment, 0, e.Comment.Length);
                }
                long cdSize = outp.Position - cdOff;
                byte[] eocd = new byte[22];
                PutU32(eocd, 0, 0x06054b50);
                PutU16(eocd, 4, 0); PutU16(eocd, 6, 0);
                PutU16(eocd, 8, (ushort)cd.Count); PutU16(eocd, 10, (ushort)cd.Count);
                PutU32(eocd, 12, (uint)cdSize); PutU32(eocd, 16, (uint)cdOff);
                PutU16(eocd, 20, 0);
                outp.Write(eocd, 0, 22);
                return outp.Position;
            }
        }

        static ZipEntry Clone(ZipEntry e)
        {
            ZipEntry n = new ZipEntry();
            n.Name = e.Name; n.NameStr = e.NameStr; n.VerMade = e.VerMade;
            n.VerNeed = e.VerNeed; n.Flags = e.Flags; n.Method = e.Method;
            n.MTime = e.MTime; n.MDate = e.MDate; n.Crc = e.Crc;
            n.CSize = e.CSize; n.USize = e.USize; n.DStart = e.DStart;
            n.IAttr = e.IAttr; n.EAttr = e.EAttr; n.Lho = e.Lho;
            n.Extra = e.Extra; n.Comment = e.Comment;
            return n;
        }

        static void WriteLocal(Stream o, int verNeed, int flags, int method, int mtime, int mdate,
                               uint crc, int csize, int usize, byte[] name, byte[] extra, byte[] body)
        {
            byte[] h = new byte[30];
            PutU32(h, 0, 0x04034b50);
            PutU16(h, 4, verNeed); PutU16(h, 6, flags); PutU16(h, 8, method);
            PutU16(h, 10, mtime); PutU16(h, 12, mdate);
            PutU32(h, 14, crc);
            PutU32(h, 18, (uint)csize); PutU32(h, 22, (uint)usize);
            PutU16(h, 26, name.Length); PutU16(h, 28, extra.Length);
            o.Write(h, 0, 30);
            o.Write(name, 0, name.Length);
            if (extra.Length > 0) o.Write(extra, 0, extra.Length);
            o.Write(body, 0, body.Length);
        }

        static void PutU16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        static void CopyBytes(Stream s, Stream d, long count) { CopyBytes(s, d, count, new byte[1 << 20]); }
        static void CopyBytes(Stream s, Stream d, long count, byte[] buf)
        {
            while (count > 0)
            {
                int want = (int)Math.Min(buf.Length, count);
                int n = s.Read(buf, 0, want);
                if (n <= 0) throw new EndOfStreamException();
                d.Write(buf, 0, n);
                count -= n;
            }
        }
    }

    // ======================================================= 存档 Lua 读写 ====
    static class LuaSave
    {
        public static Dictionary<object, object> Parse(string text)
        {
            int start = text.IndexOf('{');
            if (start < 0) throw new Exception("存档里找不到 Lua 表");
            int i = start;
            object v = ParseValue(text, ref i);
            Dictionary<object, object> t = v as Dictionary<object, object>;
            if (t == null) throw new Exception("存档顶层不是表");
            return t;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new Exception("存档意外结束");
            char c = s[i];
            if (c == '{') return ParseTable(s, ref i);
            if (c == '"' || c == '\'') return ParseString(s, ref i);
            if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 3 <= s.Length && s.Substring(i, 3) == "nil") { i += 3; return null; }
            int st = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' ||
                                    s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            string num = s.Substring(st, i - st);
            if (num.Length == 0) throw new Exception("无法解析的值 @" + st);
            long lv;
            if (num.IndexOf('.') < 0 && num.IndexOf('e') < 0 && num.IndexOf('E') < 0 &&
                long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                return lv;
            return double.Parse(num, CultureInfo.InvariantCulture);
        }

        static string ParseString(string s, ref int i)
        {
            char quote = s[i++];
            StringBuilder sb = new StringBuilder();
            while (i < s.Length && s[i] != quote)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(s[i++]);
            }
            i++;
            return sb.ToString();
        }

        static Dictionary<object, object> ParseTable(string s, ref int i)
        {
            Dictionary<object, object> t = new Dictionary<object, object>();
            i++;
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("表没有闭合");
                if (s[i] == '}') { i++; return t; }
                if (s[i] != '[') throw new Exception("表项格式异常 @" + i);
                i++;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("表项没有 key");
                object key;
                if (s[i] == '"' || s[i] == '\'') key = ParseString(s, ref i);
                else
                {
                    int st = i;
                    while (i < s.Length && s[i] != ']') i++;
                    if (i >= s.Length) throw new Exception("key 没有闭合");
                    key = long.Parse(s.Substring(st, i - st).Trim(), CultureInfo.InvariantCulture);
                }
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '=') i++;
                t[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && (s[i] == ';' || s[i] == ',')) i++;
            }
        }

        public static string Serialize(Dictionary<object, object> t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("local obj1 = ");
            Emit(sb, t, 0);
            sb.Append("\nreturn obj1\n");
            return sb.ToString();
        }

        static void Emit(StringBuilder sb, object v, int indent)
        {
            Dictionary<object, object> t = v as Dictionary<object, object>;
            if (t != null)
            {
                if (t.Count == 0)
                {
                    sb.Append("{\n").Append(Tabs(indent)).Append('}');
                    return;
                }
                sb.Append("{\n");
                foreach (object k in SortKeys(t.Keys))
                {
                    sb.Append(Tabs(indent + 1)).Append('[');
                    if (k is long) sb.Append(((long)k).ToString(CultureInfo.InvariantCulture));
                    else sb.Append('"').Append(Escape(Convert.ToString(k))).Append('"');
                    sb.Append("] = ");
                    Emit(sb, t[k], indent + 1);
                    sb.Append(";\n");
                }
                sb.Append(Tabs(indent)).Append('}');
                return;
            }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v == null) { sb.Append("nil"); return; }
            if (v is long) { sb.Append(((long)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 9e15)
                    sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            sb.Append('"').Append(Escape(Convert.ToString(v))).Append('"');
        }

        static string Tabs(int n) { return new string('\t', n); }

        static string Escape(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append("\\" + ((int)c).ToString("000"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static List<object> SortKeys(IEnumerable<object> keys)
        {
            List<object> list = new List<object>(keys);
            list.Sort(delegate(object a, object b)
            {
                bool an = a is long, bn = b is long;
                if (an && bn) return ((long)a).CompareTo((long)b);
                if (an) return -1;
                if (bn) return 1;
                return string.CompareOrdinal(Convert.ToString(a), Convert.ToString(b));
            });
            return list;
        }
    }

    // ========================================================= 存档一键操作 ====
    static class SaveOps
    {
        public const long MAX_GEMS = 999999;
        public const long MAX_HERO_XP = 115300;
        public const long HERO_SKILL_MAX = 3;
        public const long MAX_BAG = 99;
        public const long MAX_UPGRADE_TIER = 5;
        public const int LEVELS = 22;
        static readonly long[] MODES = new long[] { 1, 2, 3 };

        static Dictionary<object, object> Table(object parent, string key)
        {
            Dictionary<object, object> d = parent as Dictionary<object, object>;
            if (d == null) return null;
            object v;
            if (d.TryGetValue(key, out v)) return v as Dictionary<object, object>;
            return null;
        }

        public static long TotalStars(Dictionary<object, object> t)
        {
            long total = 0;
            Dictionary<object, object> levels = Table(t, "levels");
            if (levels == null) return 0;
            foreach (KeyValuePair<object, object> kv in levels)
            {
                Dictionary<object, object> e = kv.Value as Dictionary<object, object>;
                if (e == null) continue;
                object st;
                if (e.TryGetValue("stars", out st) && st is long) total += (long)st;
                foreach (long m in MODES)
                {
                    if (m == 1) continue;
                    object f;
                    if (e.TryGetValue(m, out f) && f != null) total++;
                }
            }
            return total;
        }

        public static long GetGems(Dictionary<object, object> t)
        {
            object v; t.TryGetValue("gems", out v);
            return v is long ? (long)v : 0;
        }

        public static string MaxGems(Dictionary<object, object> t)
        {
            long old = GetGems(t);
            t["gems"] = MAX_GEMS;
            return "宝石 " + old + " -> " + MAX_GEMS;
        }

        public static string MaxStars(Dictionary<object, object> t)
        {
            Dictionary<object, object> levels = Table(t, "levels");
            if (levels == null) { levels = new Dictionary<object, object>(); t["levels"] = levels; }
            for (int i = 1; i <= LEVELS; i++)
            {
                Dictionary<object, object> e = null;
                object cur;
                if (levels.TryGetValue((long)i, out cur)) e = cur as Dictionary<object, object>;
                if (e == null) { e = new Dictionary<object, object>(); levels[(long)i] = e; }
                foreach (long m in MODES) e[m] = 1L;
                e["stars"] = 3L;
            }
            return LEVELS + " 关全部通关 + 3 星（共 " + TotalStars(t) + " 星）";
        }

        public static string MaxUpgrades(Dictionary<object, object> t)
        {
            Dictionary<object, object> up = Table(t, "upgrades");
            if (up == null) { up = new Dictionary<object, object>(); t["upgrades"] = up; }
            string[] names = { "archers", "barracks", "engineers", "mages", "rain", "reinforcements" };
            foreach (string n in names) up[n] = MAX_UPGRADE_TIER;
            return "升级树全部 " + MAX_UPGRADE_TIER + " 级";
        }

        public static string MaxHeroes(Dictionary<object, object> t)
        {
            Dictionary<object, object> heroes = Table(t, "heroes");
            if (heroes == null) return "存档里没有英雄数据";
            Dictionary<object, object> status = Table(heroes, "status");
            if (status == null) return "存档里没有英雄数据";
            int n = 0;
            foreach (KeyValuePair<object, object> kv in status)
            {
                Dictionary<object, object> st = kv.Value as Dictionary<object, object>;
                if (st == null) continue;
                st["xp"] = MAX_HERO_XP;
                Dictionary<object, object> skills = Table(st, "skills");
                if (skills != null)
                {
                    List<object> ks = new List<object>(skills.Keys);
                    foreach (object k in ks) skills[k] = HERO_SKILL_MAX;
                }
                n++;
            }
            return n + " 位英雄满级（XP " + MAX_HERO_XP + "，技能全 " + HERO_SKILL_MAX + " 级）";
        }

        public static string MaxBag(Dictionary<object, object> t)
        {
            Dictionary<object, object> bag = Table(t, "bag");
            if (bag == null) { bag = new Dictionary<object, object>(); t["bag"] = bag; }
            List<object> ks = new List<object>(bag.Keys);
            foreach (object k in ks) bag[k] = MAX_BAG;
            return "道具拉满（" + ks.Count + " 项）";
        }

        public static string UnlockAchievements(Dictionary<object, object> t)
        {
            Dictionary<object, object> a = Table(t, "achievements");
            if (a == null) return "没有成就数据";
            List<object> ks = new List<object>(a.Keys);
            foreach (object k in ks) a[k] = true;
            return "解锁 " + ks.Count + " 个成就";
        }

        public static List<string> ApplyAll(Dictionary<object, object> t)
        {
            List<string> log = new List<string>();
            log.Add("宝石拉满: " + MaxGems(t));
            log.Add("全关卡三星: " + MaxStars(t));
            log.Add("升级树: " + MaxUpgrades(t));
            log.Add("英雄: " + MaxHeroes(t));
            log.Add("道具: " + MaxBag(t));
            log.Add("成就: " + UnlockAchievements(t));
            return log;
        }
    }

    // ============================================================ 扁平按钮 ====
    class FlatBtn : Control
    {
        public Color Fill = Theme.Slate;
        public Color TextColor = Theme.Text;
        public int Radius = 7;
        bool hover, down;

        public FlatBtn()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = Theme.F(9F);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = Enabled ? Fill : Color.FromArgb(48, 53, 63);
            if (Enabled && down) c = Ui.Mix(Fill, Color.Black, 0.25);
            else if (Enabled && hover) c = Ui.Mix(Fill, Color.White, 0.15);
            using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (SolidBrush b = new SolidBrush(c))
                e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(0, 0, Width, Height), Enabled ? TextColor : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // ============================================================= 功能行 ====
    class FeatureRow : Control
    {
        public int Index;
        public string Title = "";
        public string Desc = "";
        public string Hotkey = "";
        public bool IsOn;
        public event EventHandler Toggled;
        public event EventHandler HotkeyClicked;
        public event EventHandler HoverChanged;

        bool hover, hkHover;

        public bool Hovered { get { return hover; } }

        public FeatureRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = Ui.S(34);
        }

        Rectangle SwitchRect
        {
            get { return new Rectangle(Width - Ui.S(46), (Height - Ui.S(18)) / 2, Ui.S(38), Ui.S(18)); }
        }
        Rectangle HotkeyRect
        {
            get { return new Rectangle(Width - Ui.S(100), (Height - Ui.S(20)) / 2, Ui.S(46), Ui.S(20)); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = HotkeyRect.Contains(e.Location);
            if (h != hkHover) { hkHover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true; Invalidate();
            if (HoverChanged != null) HoverChanged(this, EventArgs.Empty);
            base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false; hkHover = false; Invalidate();
            if (HoverChanged != null) HoverChanged(this, EventArgs.Empty);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (HotkeyRect.Contains(e.Location))
            {
                if (HotkeyClicked != null) HotkeyClicked(this, EventArgs.Empty);
                return;
            }
            IsOn = !IsOn;
            if (Toggled != null) Toggled(this, EventArgs.Empty);
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath bg = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(8)))
            using (SolidBrush b = new SolidBrush(hover ? Theme.CardHover : Theme.Card))
                g.FillPath(b, bg);

            if (IsOn)
            {
                using (GraphicsPath bar = Ui.Round(
                    new Rectangle(Ui.S(1), Ui.S(4), Ui.S(3), Height - Ui.S(8)), Ui.S(2)))
                using (SolidBrush b = new SolidBrush(Theme.Green))
                    g.FillPath(b, bar);
            }

            Rectangle badge = new Rectangle(Ui.S(9), (Height - Ui.S(20)) / 2, Ui.S(22), Ui.S(20));
            using (GraphicsPath bp = Ui.Round(badge, Ui.S(5)))
            using (SolidBrush b = new SolidBrush(IsOn ? Theme.Green : Theme.Slate))
                g.FillPath(b, bp);
            TextRenderer.DrawText(g, (Index + 1).ToString("00"), Theme.FB(8F), badge,
                IsOn ? Color.FromArgb(12, 40, 26) : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int x = Ui.S(32);
            int rightLimit = HotkeyRect.Left - Ui.S(6);
            Rectangle titleR = new Rectangle(x, 0, Math.Max(10, rightLimit - x), Height);
            TextRenderer.DrawText(g, Title, Theme.FB(9F), titleR,
                IsOn ? Theme.Text : Color.FromArgb(200, 206, 218),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            Rectangle hk = HotkeyRect;
            using (GraphicsPath hp = Ui.Round(hk, Ui.S(6)))
            {
                using (SolidBrush b = new SolidBrush(hkHover ? Theme.CardHover : Theme.Bg))
                    g.FillPath(b, hp);
                using (Pen p = new Pen(hkHover ? Theme.Blue : Theme.Border))
                    g.DrawPath(p, hp);
            }
            TextRenderer.DrawText(g, Hotkey, Theme.F(8F), hk,
                hkHover ? Theme.Blue : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            Rectangle sw = SwitchRect;
            using (GraphicsPath sp = Ui.Round(sw, sw.Height / 2))
            using (SolidBrush b = new SolidBrush(IsOn ? Theme.Green : Color.FromArgb(60, 67, 80)))
                g.FillPath(b, sp);
            int knob = sw.Height - Ui.S(6);
            int kx = IsOn ? sw.Right - knob - Ui.S(3) : sw.Left + Ui.S(3);
            using (SolidBrush b = new SolidBrush(IsOn ? Color.White : Color.FromArgb(150, 158, 172)))
                g.FillEllipse(b, kx, sw.Top + Ui.S(3), knob, knob);
        }
    }

    // =========================================================== 分组标题 ====
    class Caption : Control
    {
        public Caption()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(20);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Ui.Round(new Rectangle(0, Ui.S(6), Ui.S(3), Ui.S(10)), Ui.S(2)))
            using (SolidBrush b = new SolidBrush(Theme.Blue))
                e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, Text, Theme.FB(8.5F),
                new Rectangle(Ui.S(10), 0, Width - Ui.S(10), Height), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    // ========================================================== 头部横幅 ====
    class HeaderPanel : Panel
    {
        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle r = ClientRectangle;
            using (LinearGradientBrush b = new LinearGradientBrush(r, Theme.HeaderTop, Theme.HeaderBottom,
                                                                    LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(b, r);
            using (SolidBrush b = new SolidBrush(Theme.Blue))
                e.Graphics.FillRectangle(b, new Rectangle(0, Height - Ui.S(2), Width, Ui.S(2)));

            TextRenderer.DrawText(e.Graphics, "王国保卫战：前线", Theme.FB(15F),
                new Rectangle(Ui.S(22), Ui.S(13), Width - Ui.S(190), Ui.S(30)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics,
                "Kingdom Rush Frontiers  ·  kr2-desktop-6.4.46  ·  单文件修改器",
                Theme.F(8F), new Rectangle(Ui.S(24), Ui.S(45), Width - Ui.S(190), Ui.S(18)),
                Color.FromArgb(150, 172, 205), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            base.OnPaint(e);
        }
    }

    // ======================================================== 热键设置框 ====
    class HotkeyDialog : Form
    {
        public int KeyCode;
        public int Mods;

        public HotkeyDialog(int key, int mods)
        {
            Text = "设置热键";
            ClientSize = new Size(Ui.S(340), Ui.S(140));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;

            Label tip = new Label();
            tip.ForeColor = Theme.Text;
            tip.Font = Theme.FB(11F);
            tip.Text = "请按下新的热键…";
            tip.SetBounds(Ui.S(22), Ui.S(30), Ui.S(296), Ui.S(28));
            Controls.Add(tip);

            Label sub = new Label();
            sub.ForeColor = Theme.Muted;
            sub.Font = Theme.F(8.5F);
            sub.Text = "Esc 取消      Backspace 恢复默认";
            sub.SetBounds(Ui.S(22), Ui.S(66), Ui.S(296), Ui.S(22));
            Controls.Add(sub);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }
                if (e.KeyCode == Keys.Back)
                {
                    KeyCode = 0; Mods = 0;
                    DialogResult = DialogResult.OK; Close(); return;
                }
                if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.ControlKey ||
                    e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                    return;
                KeyCode = (int)e.KeyCode;
                Mods = 0;
                if ((Control.ModifierKeys & Keys.Control) != 0) Mods |= 2;
                if ((Control.ModifierKeys & Keys.Alt) != 0) Mods |= 1;
                if ((Control.ModifierKeys & Keys.Shift) != 0) Mods |= 4;
                DialogResult = DialogResult.OK;
                Close();
            };
        }
    }

    // ================================================================ 主界面 ====
    class MainForm : Form
    {
        const int WM_HOTKEY = 0x0312;
        const int FEATURES = 12;
        const int VK_F1 = 0x70;
        const string EXE_NAME = "Kingdom Rush Frontiers.exe";
        const string BACKUP_SUFFIX = ".dsh-backup";
        const string LOG_NAME = "cheat_log.txt";

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        static readonly string[] Titles = {
            "无限金币", "无限生命", "秒杀敌人", "友军无敌", "我方满血", "敌人脆弱",
            "敌人减速", "敌人定身", "技能无冷却", "分数拉满", "英雄经验倍率", "解锁全部防御塔"
        };
        static readonly string[] Descs = {
            "金币锁定 999999，造塔升级随便点",
            "生命锁定 99，敌人漏过去也不掉血",
            "持续清空地图上所有敌人",
            "我方士兵与英雄免疫一切伤害",
            "我方单位血量始终回满",
            "敌人血量降到 10%，一碰就死",
            "敌人移动速度减半",
            "敌人完全无法移动",
            "陨石雨 / 援军 / 英雄技能随时可用",
            "得分锁定 999999",
            "英雄经验获取 ×50，快速升到 10 级",
            "解锁全部防御塔（含未解锁的高级塔）"
        };

        List<FeatureRow> rows = new List<FeatureRow>();
        TextBox txtLog;
        Label lblGame, lblInstall;
        FlatBtn btnActivate;
        Panel statusBar;
        CheckBox chkTop, chkSound;
        string gameDir, saveDir, slotPath, logPath;
        int[] keyCodes = new int[FEATURES];
        int[] keyMods = new int[FEATURES];
        bool soundOn = true;
        int statusKind;
        string statusText = "就绪";

        public MainForm()
        {
            logPath = Path.Combine(Ctl.Dir, LOG_NAME);

            using (Graphics g = CreateGraphics())
                Ui.Scale = Math.Max(1f, g.DpiX / 96f);

            Text = "《王国保卫战：前线》修改器";
            ClientSize = new Size(Ui.S(660), Ui.S(650));
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(9F);

            HeaderPanel header = new HeaderPanel();
            header.SetBounds(0, 0, ClientSize.Width, Ui.S(84));
            Controls.Add(header);

            btnActivate = new FlatBtn();
            btnActivate.Text = "激活修改器";
            btnActivate.Font = Theme.FB(10F);
            btnActivate.Fill = Theme.Blue;
            btnActivate.SetBounds(ClientSize.Width - Ui.S(158), Ui.S(23), Ui.S(136), Ui.S(40));
            btnActivate.Click += delegate { ActivateTrainer(); };
            header.Controls.Add(btnActivate);

            Caption cap1 = new Caption();
            cap1.Text = "游戏内实时功能（鼠标悬停查看说明）";
            cap1.SetBounds(Ui.S(16), Ui.S(92), Ui.S(420), Ui.S(20));
            Controls.Add(cap1);

            Panel list = new Panel();
            list.SetBounds(Ui.S(14), Ui.S(114), ClientSize.Width - Ui.S(28), Ui.S(232));
            list.BackColor = Theme.Bg;
            Controls.Add(list);

            int cw = (list.ClientSize.Width - Ui.S(8)) / 2;
            int ch = Ui.S(34), cy = Ui.S(5);
            for (int i = 0; i < FEATURES; i++)
            {
                FeatureRow r = new FeatureRow();
                r.Index = i;
                r.Title = Titles[i];
                r.Desc = Descs[i];
                r.Hotkey = "F" + (i + 1);
                r.Width = cw;
                r.Left = (i % 2) * (cw + Ui.S(8));
                r.Top = (i / 2) * (ch + cy);
                int idx = i;
                r.Toggled += delegate { OnRowToggled(idx); };
                r.HotkeyClicked += delegate { RebindHotkey(idx); };
                r.HoverChanged += delegate { OnRowHover(idx); };
                list.Controls.Add(r);
                rows.Add(r);
            }

            // ---- 一次性战斗操作 ----
            Caption capA = new Caption();
            capA.Text = "战斗操作（点一下立即生效，与开关无关）";
            capA.SetBounds(Ui.S(16), Ui.S(354), Ui.S(440), Ui.S(20));
            Controls.Add(capA);

            string[] actNames = { "清空全场敌人", "我方全体满血", "技能立刻就绪", "全部关闭" };
            string[] actCmds = { "kill", "heal", "power", "" };
            for (int i = 0; i < actNames.Length; i++)
            {
                FlatBtn b = new FlatBtn();
                b.Text = actNames[i];
                b.Fill = (i == 3) ? Theme.Card : Theme.Slate;
                b.SetBounds(Ui.S(14) + i * (Ui.S(152) + Ui.S(8)), Ui.S(378), Ui.S(152), Ui.S(32));
                string cmd = actCmds[i];
                b.Click += delegate { OnAction(cmd); };
                Controls.Add(b);
            }

            Caption cap2 = new Caption();
            cap2.Text = "存档一键修改（改前请完全退出游戏）";
            cap2.SetBounds(Ui.S(16), Ui.S(418), Ui.S(420), Ui.S(20));
            Controls.Add(cap2);

            string[] saveNames = { "宝石拉满", "全关卡三星", "升级树全满", "英雄满级",
                                   "道具拉满", "一键全解锁", "还原最近备份", "打开存档目录" };
            int bw = Ui.S(152), bh = Ui.S(32), gap = Ui.S(8);
            for (int i = 0; i < saveNames.Length; i++)
            {
                FlatBtn b = new FlatBtn();
                b.Text = saveNames[i];
                b.Fill = (i == 5) ? Theme.Green : Theme.Slate;
                if (i == 5) b.TextColor = Color.FromArgb(10, 35, 22);
                int col = i % 4, row = i / 4;
                b.SetBounds(Ui.S(14) + col * (bw + gap), Ui.S(442) + row * (bh + gap), bw, bh);
                string cap = saveNames[i];
                b.Click += delegate { OnSaveButton(cap); };
                Controls.Add(b);
            }

            Caption cap3 = new Caption();
            cap3.Text = "游戏文件与选项";
            cap3.SetBounds(Ui.S(16), Ui.S(520), Ui.S(300), Ui.S(20));
            Controls.Add(cap3);

            lblGame = new Label();
            lblGame.ForeColor = Theme.Muted;
            lblGame.AutoEllipsis = true;
            lblGame.SetBounds(Ui.S(18), Ui.S(546), Ui.S(400), Ui.S(17));
            Controls.Add(lblGame);

            lblInstall = new Label();
            lblInstall.ForeColor = Theme.Muted;
            lblInstall.AutoEllipsis = true;
            lblInstall.SetBounds(Ui.S(18), Ui.S(564), Ui.S(400), Ui.S(17));
            Controls.Add(lblInstall);

            FlatBtn bUn = new FlatBtn();
            bUn.Text = "卸载并还原游戏";
            bUn.SetBounds(Ui.S(18), Ui.S(585), Ui.S(142), Ui.S(30));
            bUn.Click += delegate { Uninstall(); };
            Controls.Add(bUn);

            FlatBtn bLog = new FlatBtn();
            bLog.Text = "查看运行日志";
            bLog.SetBounds(Ui.S(168), Ui.S(585), Ui.S(128), Ui.S(30));
            bLog.Click += delegate { ShowLog(); };
            Controls.Add(bLog);

            FlatBtn bHelp = new FlatBtn();
            bHelp.Text = "使用说明";
            bHelp.SetBounds(Ui.S(304), Ui.S(585), Ui.S(112), Ui.S(30));
            bHelp.Click += delegate { ShowHelp(); };
            Controls.Add(bHelp);

            chkTop = new CheckBox();
            chkTop.Text = "窗口置顶";
            chkTop.ForeColor = Theme.Muted;
            chkTop.FlatStyle = FlatStyle.Flat;
            chkTop.SetBounds(Ui.S(432), Ui.S(546), Ui.S(104), Ui.S(22));
            chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; SaveSettings(); };
            Controls.Add(chkTop);

            chkSound = new CheckBox();
            chkSound.Text = "按键音效";
            chkSound.ForeColor = Theme.Muted;
            chkSound.FlatStyle = FlatStyle.Flat;
            chkSound.Checked = true;
            chkSound.SetBounds(Ui.S(432), Ui.S(570), Ui.S(104), Ui.S(22));
            chkSound.CheckedChanged += delegate { soundOn = chkSound.Checked; SaveSettings(); };
            Controls.Add(chkSound);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(16, 19, 24);
            txtLog.ForeColor = Theme.Muted;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.Font = Theme.F(8F);
            txtLog.SetBounds(Ui.S(544), Ui.S(546), Ui.S(100), Ui.S(69));
            Controls.Add(txtLog);

            statusBar = new Panel();
            statusBar.SetBounds(0, ClientSize.Height - Ui.S(26), ClientSize.Width, Ui.S(26));
            statusBar.BackColor = Color.FromArgb(17, 20, 25);
            statusBar.Paint += delegate(object s, PaintEventArgs e) { PaintStatus(e.Graphics); };
            Controls.Add(statusBar);

            Load += delegate { OnLoad_(); };
            FormClosing += delegate { UnregisterAll(); };

            Timer timer = new Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { RefreshRuntime(); };
            timer.Start();
        }

        void PaintStatus(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color dot = statusKind == 1 ? Theme.Green : (statusKind == 2 ? Color.FromArgb(233, 168, 72) : Theme.Muted);
            using (SolidBrush b = new SolidBrush(dot))
                g.FillEllipse(b, Ui.S(14), Ui.S(9), Ui.S(8), Ui.S(8));
            TextRenderer.DrawText(g, statusText, Theme.F(8.5F), new Rectangle(Ui.S(28), 0, Width, Height),
                dot, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "热键可点击行内胶囊自定义", Theme.F(8F),
                new Rectangle(0, 0, Width - Ui.S(14), Height), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void SetStatus(string text, int kind)
        {
            statusText = text;
            statusKind = kind;
            if (statusBar != null) statusBar.Invalidate();
        }

        // ------------------------------------------------------------ 启动 ----
        void OnLoad_()
        {
            LoadHotkeys();
            LoadSettings();
            for (int i = 0; i < FEATURES; i++) RegisterOne(i);

            bool[] v = Ctl.Read();
            for (int i = 0; i < rows.Count && i < v.Length; i++) rows[i].IsOn = v[i];

            saveDir = DetectSaveDir();
            slotPath = saveDir == null ? null : Path.Combine(saveDir, "slot_1.lua");
            gameDir = DetectGameDir();

            Log("存档目录: " + (saveDir == null ? "未找到" : saveDir));
            Log("游戏目录: " + (gameDir == null ? "未找到" : gameDir));

            RefreshInstallState();
            SetEnabledState(false);
            if (gameDir != null && IsInstalled(gameDir)) ActivateTrainer();
            else SetStatus("未激活：请点右上角「激活修改器」", 2);
            RefreshRuntime();
        }

        void LoadSettings()
        {
            Dictionary<string, string> m = Ctl.LoadMap(Ctl.SetFile);
            string t, s;
            if (m.TryGetValue("topmost", out t)) chkTop.Checked = (t == "1");
            if (m.TryGetValue("sound", out s)) chkSound.Checked = (s == "1");
            soundOn = chkSound.Checked;
            TopMost = chkTop.Checked;
        }

        void SaveSettings()
        {
            Dictionary<string, string> m = new Dictionary<string, string>();
            m["topmost"] = chkTop.Checked ? "1" : "0";
            m["sound"] = chkSound.Checked ? "1" : "0";
            Ctl.SaveMap(Ctl.SetFile, m);
        }

        void LoadHotkeys()
        {
            for (int i = 0; i < FEATURES; i++)
            {
                keyCodes[i] = VK_F1 + i;
                keyMods[i] = 0;
            }
            Dictionary<string, string> m = Ctl.LoadMap(Ctl.HotkeyFile);
            for (int i = 0; i < FEATURES; i++)
            {
                string v;
                if (!m.TryGetValue(i.ToString(), out v)) continue;
                string[] parts = v.Split('|');
                int k, md;
                if (parts.Length == 2 && int.TryParse(parts[0], out k) && int.TryParse(parts[1], out md)
                    && k != 0)
                {
                    keyCodes[i] = k;
                    keyMods[i] = md;
                }
            }
            RefreshHotkeyLabels();
        }

        void SaveHotkeys()
        {
            Dictionary<string, string> m = new Dictionary<string, string>();
            for (int i = 0; i < FEATURES; i++)
                m[i.ToString()] = keyCodes[i] + "|" + keyMods[i];
            Ctl.SaveMap(Ctl.HotkeyFile, m);
        }

        string HotkeyLabel(int i)
        {
            if (keyCodes[i] == 0) return "无";
            string s = "";
            if ((keyMods[i] & 2) != 0) s += "Ctrl+";
            if ((keyMods[i] & 1) != 0) s += "Alt+";
            if ((keyMods[i] & 4) != 0) s += "Shift+";
            string name = ((Keys)keyCodes[i]).ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            return s + name;
        }

        void RefreshHotkeyLabels()
        {
            for (int i = 0; i < rows.Count; i++) rows[i].Hotkey = HotkeyLabel(i);
        }

        void RegisterOne(int i)
        {
            if (keyCodes[i] == 0) return;
            try { RegisterHotKey(Handle, i + 1, keyMods[i], keyCodes[i]); } catch { }
        }

        void UnregisterAll()
        {
            for (int i = 0; i < FEATURES; i++)
            {
                try { UnregisterHotKey(Handle, i + 1); } catch { }
            }
        }

        void RebindHotkey(int idx)
        {
            UnregisterAll();
            using (HotkeyDialog d = new HotkeyDialog(keyCodes[idx], keyMods[idx]))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    if (d.KeyCode == 0) { keyCodes[idx] = VK_F1 + idx; keyMods[idx] = 0; }
                    else { keyCodes[idx] = d.KeyCode; keyMods[idx] = d.Mods; }
                    for (int i = 0; i < FEATURES; i++)
                    {
                        if (i != idx && keyCodes[i] == keyCodes[idx] && keyMods[i] == keyMods[idx])
                        { keyCodes[i] = 0; keyMods[i] = 0; }
                    }
                    SaveHotkeys();
                    RefreshHotkeyLabels();
                    Log("热键: " + Titles[idx] + " -> " + HotkeyLabel(idx));
                }
            }
            for (int i = 0; i < FEATURES; i++) RegisterOne(i);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int idx = m.WParam.ToInt32() - 1;
                if (idx >= 0 && idx < rows.Count)
                {
                    rows[idx].IsOn = !rows[idx].IsOn;
                    rows[idx].Invalidate();
                    OnRowToggled(idx);
                }
            }
            base.WndProc(ref m);
        }

        void OnRowToggled(int idx)
        {
            WriteControls();
            if (soundOn)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
            Log("开关: " + Titles[idx] + " = " + (rows[idx].IsOn ? "开" : "关"));
            SetStatus(Titles[idx] + " 已" + (rows[idx].IsOn ? "开启" : "关闭"), rows[idx].IsOn ? 1 : 0);
        }

        void OnRowHover(int idx)
        {
            if (rows[idx].Hovered)
                SetStatus(Titles[idx] + " —— " + Descs[idx], rows[idx].IsOn ? 1 : 0);
            else
                RefreshRuntime();
        }

        // 一次性操作: 写指令文件, 游戏读到执行一次
        void OnAction(string cmd)
        {
            if (cmd == "")
            {
                foreach (FeatureRow r in rows) { r.IsOn = false; r.Invalidate(); }
                WriteControls();
                Log("已关闭全部功能");
                SetStatus("已关闭全部功能", 0);
                return;
            }
            if (!rows[0].Enabled)
            {
                SetStatus("请先点右上角「激活修改器」", 2);
                return;
            }
            string name = cmd == "kill" ? "清空全场敌人"
                        : (cmd == "heal" ? "我方全体满血" : "技能立刻就绪");
            Ctl.SendCommand(cmd);
            if (soundOn) { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } }
            Log("执行: " + name);
            SetStatus("已发送「" + name + "」，战斗中 0.5 秒内生效", 1);
        }

        void WriteControls()
        {
            bool[] v = new bool[FEATURES];
            for (int i = 0; i < FEATURES; i++) v[i] = rows[i].IsOn;
            Ctl.Write(v);
        }

        // ------------------------------------------------------------ 状态 ----
        void SetEnabledState(bool on)
        {
            foreach (FeatureRow r in rows) r.Enabled = on;
            btnActivate.Text = on ? "已激活" : "激活修改器";
            btnActivate.Enabled = !on;
            btnActivate.Fill = on ? Theme.Card : Theme.Blue;
            btnActivate.TextColor = on ? Theme.Muted : Theme.Text;
            btnActivate.Invalidate();
        }

        string DetectSaveDir()
        {
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] cands = { Path.Combine(appdata, "kingdom_rush_frontiers"),
                               Path.Combine(appdata, "Kingdom Rush Frontiers") };
            foreach (string d in cands)
            {
                if (!Directory.Exists(d)) continue;
                if (Directory.GetFiles(d, "slot_*.lua").Length > 0) return d;
            }
            return cands[0];
        }

        string DetectGameDir()
        {
            List<string> roots = new List<string>();
            try
            {
                object sp = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (sp != null)
                {
                    string steam = Convert.ToString(sp).Replace('/', '\\');
                    roots.Add(Path.Combine(steam, "steamapps", "common"));
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (string line in File.ReadAllLines(vdf))
                        {
                            int k = line.IndexOf("\"path\"");
                            if (k < 0) continue;
                            int a = line.IndexOf('"', k + 6);
                            if (a < 0) continue;
                            int b = line.IndexOf('"', a + 1);
                            if (b < 0) continue;
                            roots.Add(Path.Combine(line.Substring(a + 1, b - a - 1).Replace("\\\\", "\\"),
                                                   "steamapps", "common"));
                        }
                    }
                }
            }
            catch { }
            foreach (string r in roots)
            {
                string d = Path.Combine(r, "Kingdom Rush Frontiers");
                if (File.Exists(Path.Combine(d, EXE_NAME))) return d;
            }
            return null;
        }

        bool IsInstalled(string dir)
        {
            return File.Exists(Path.Combine(dir, EXE_NAME + BACKUP_SUFFIX));
        }

        bool IsRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("Kingdom Rush Frontiers").Length > 0; }
            catch { return false; }
        }

        void RefreshInstallState()
        {
            if (gameDir == null)
            {
                lblGame.Text = "游戏：未找到（请确认已安装 Steam 版）";
                lblInstall.Text = "";
                return;
            }
            lblGame.Text = "游戏：" + gameDir;
            lblInstall.Text = IsInstalled(gameDir)
                ? "状态：已安装 · 原版已备份，可随时卸载还原"
                : "状态：未安装 · 点右上角「激活修改器」";
        }

        void RefreshRuntime()
        {
            if (!IsRunning())
            {
                SetStatus(rows[0].Enabled ? "游戏未运行" : "未激活：请先点「激活修改器」", 0);
                return;
            }
            string tail = ReadLogTail(4000);
            int p = tail.LastIndexOf("payload v");
            if (p >= 0) tail = tail.Substring(p);
            if (tail.IndexOf("CAPTURED store") >= 0)
            {
                int on = 0;
                foreach (FeatureRow r in rows) if (r.IsOn) on++;
                SetStatus("游戏运行中 · 战场已锁定 · " + on + " 项功能开启", 1);
            }
            else SetStatus("游戏运行中 · 等待进入关卡", 2);
        }

        string ReadLogTail(int bytes)
        {
            try
            {
                if (!File.Exists(logPath)) return "";
                using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - bytes);
                    fs.Position = start;
                    byte[] buf = new byte[fs.Length - start];
                    fs.Read(buf, 0, buf.Length);
                    return Encoding.UTF8.GetString(buf);
                }
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------ 激活 ----
        void ActivateTrainer()
        {
            if (gameDir == null)
            {
                MessageBox.Show(this, "没有找到游戏安装目录。\n请确认已通过 Steam 安装本游戏。",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                WriteControls();
                if (!IsInstalled(gameDir))
                {
                    if (IsRunning())
                        throw new Exception("游戏正在运行，请先完全退出游戏再激活。");
                    SetStatus("正在安装游戏内修改器…", 2);
                    Application.DoEvents();
                    Log("正在安装（备份原版 + 重打包）…");
                    Install(gameDir);
                    Log("安装完成。");
                }
                SetEnabledState(true);
                RefreshInstallState();
                Log("修改器已激活，勾选项与热键立即生效。");
                SetStatus("已激活 · 进入游戏关卡后自动生效", 1);
            }
            catch (Exception ex)
            {
                Log("激活失败: " + ex.Message);
                SetStatus("激活失败：" + ex.Message, 2);
                MessageBox.Show(this, "激活失败：\n" + ex.Message, Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        string PayloadText()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream s = asm.GetManifestResourceStream("KrPayload");
            if (s == null)
            {
                string[] names = asm.GetManifestResourceNames();
                if (names.Length > 0) s = asm.GetManifestResourceStream(names[0]);
            }
            if (s == null) throw new Exception("内嵌的注入脚本丢失，请重新编译本程序。");
            using (s)
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        void Install(string dir)
        {
            string exe = Path.Combine(dir, EXE_NAME);
            string backup = exe + BACKUP_SUFFIX;
            if (!File.Exists(backup)) File.Copy(exe, backup, false);

            string payload = PayloadText()
                .Replace("__CHEAT_LOG__", logPath.Replace("\\", "/"))
                .Replace("__CHEAT_CMD__", Ctl.CmdFile.Replace("\\", "/"))
                .Replace("__CHEAT_CTL__", Ctl.File_.Replace("\\", "/"));

            string tmp = exe + ".dsh-new";
            using (LoveArchive ar = new LoveArchive(backup))
            {
                ZipEntry e = ar.Find("main.lua");
                if (e == null) throw new Exception("游戏归档里没有 main.lua，版本可能不兼容。");
                byte[] orig = ar.ReadData(e);
                Dictionary<string, byte[]> rep = new Dictionary<string, byte[]>();
                rep["main.lua"] = Encoding.UTF8.GetBytes(payload);
                Dictionary<string, byte[]> add = new Dictionary<string, byte[]>();
                add["main_orig.lua"] = orig;
                ar.Rebuild(rep, add, tmp);
            }
            if (IsRunning())
            {
                File.Delete(tmp);
                throw new Exception("安装过程中检测到游戏被启动，已中止。");
            }
            File.Copy(tmp, exe, true);
            File.Delete(tmp);
        }

        void Uninstall()
        {
            if (gameDir == null) { MessageBox.Show(this, "没有找到游戏目录。"); return; }
            string exe = Path.Combine(gameDir, EXE_NAME);
            string backup = exe + BACKUP_SUFFIX;
            if (!File.Exists(backup))
            {
                MessageBox.Show(this, "没有找到备份，可能从未安装过。", Text);
                return;
            }
            if (IsRunning())
            {
                MessageBox.Show(this, "游戏正在运行，请先完全退出。", Text);
                return;
            }
            if (MessageBox.Show(this, "确定要把游戏还原成原版吗？", Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                string tmp = exe + ".dsh-restore";
                File.Copy(backup, tmp, true);
                File.Copy(tmp, exe, true);
                File.Delete(tmp);
                File.Delete(backup);
                RefreshInstallState();
                SetEnabledState(false);
                Log("已还原为原版游戏。");
                SetStatus("已还原为原版游戏", 0);
                MessageBox.Show(this, "已还原为原版游戏。", Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "还原失败：" + ex.Message, Text);
            }
        }

        // ------------------------------------------------------------ 存档 ----
        Dictionary<object, object> LoadSlot()
        {
            if (slotPath == null || !File.Exists(slotPath))
                throw new Exception("没有找到存档文件 slot_1.lua。");
            return LuaSave.Parse(File.ReadAllText(slotPath, Encoding.UTF8));
        }

        void OnSaveButton(string which)
        {
            if (which == "打开存档目录")
            {
                try
                {
                    string d = Path.GetDirectoryName(slotPath);
                    if (Directory.Exists(d))
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\"");
                }
                catch { }
                return;
            }
            if (which == "还原最近备份") { RestoreBackup(); return; }

            if (IsRunning())
            {
                if (MessageBox.Show(this,
                        "游戏似乎正在运行。\n游戏退出时会覆盖存档，修改可能丢失。\n\n仍要继续吗？",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            try
            {
                Dictionary<object, object> t = LoadSlot();
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(slotPath, slotPath + ".bak-" + stamp, true);
                List<string> lines = new List<string>();
                if (which == "一键全解锁") lines = SaveOps.ApplyAll(t);
                else if (which == "宝石拉满") lines.Add("宝石拉满: " + SaveOps.MaxGems(t));
                else if (which == "全关卡三星") lines.Add("全关卡三星: " + SaveOps.MaxStars(t));
                else if (which == "升级树全满") lines.Add("升级树: " + SaveOps.MaxUpgrades(t));
                else if (which == "英雄满级") lines.Add("英雄: " + SaveOps.MaxHeroes(t));
                else if (which == "道具拉满") lines.Add("道具: " + SaveOps.MaxBag(t));

                File.WriteAllText(slotPath, LuaSave.Serialize(t), new UTF8Encoding(false));
                Log("已备份为 slot_1.lua.bak-" + stamp);
                foreach (string l in lines) Log("  " + l);
                Log("存档已写入（宝石 " + SaveOps.GetGems(t) + "，总星 " + SaveOps.TotalStars(t) + "）");
                SetStatus(which + " 完成 · 宝石 " + SaveOps.GetGems(t) +
                          " · 总星 " + SaveOps.TotalStars(t), 1);
            }
            catch (Exception ex)
            {
                Log("存档修改失败: " + ex.Message);
                SetStatus("存档修改失败：" + ex.Message, 2);
                MessageBox.Show(this, "存档修改失败：\n" + ex.Message, Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void RestoreBackup()
        {
            try
            {
                if (slotPath == null) throw new Exception("没有找到存档目录。");
                string dir = Path.GetDirectoryName(slotPath);
                List<string> baks = new List<string>(Directory.GetFiles(dir, "slot_1.lua.bak-*"));
                if (baks.Count == 0) { MessageBox.Show(this, "没有找到备份。", Text); return; }
                baks.Sort();
                string src = baks[baks.Count - 1];
                File.Copy(src, slotPath, true);
                Log("已从 " + Path.GetFileName(src) + " 还原");
                SetStatus("已还原：" + Path.GetFileName(src), 1);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "还原失败：" + ex.Message, Text);
            }
        }

        void ShowLog()
        {
            Form f = new Form();
            f.Text = "运行日志";
            f.ClientSize = new Size(Ui.S(780), Ui.S(460));
            f.StartPosition = FormStartPosition.CenterParent;
            f.BackColor = Color.FromArgb(16, 19, 24);
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Both;
            tb.WordWrap = false;
            tb.Dock = DockStyle.Fill;
            tb.BackColor = Color.FromArgb(16, 19, 24);
            tb.ForeColor = Theme.Text;
            tb.BorderStyle = BorderStyle.None;
            tb.Font = new Font("Consolas", 9F);
            try { if (File.Exists(logPath)) tb.Text = File.ReadAllText(logPath, Encoding.UTF8); }
            catch { }
            f.Controls.Add(tb);
            f.ShowDialog(this);
        }

        void ShowHelp()
        {
            string msg =
                "【游戏内实时功能】12 项，勾选即生效，默认 F1~F12。\n" +
                "  · 点功能行右侧的胶囊可以自定义热键（Esc 取消，Backspace 恢复默认）。\n" +
                "  · 鼠标悬停在功能行上，状态栏会显示它的说明。\n" +
                "  · 只有进入战斗后才会捕获战场状态，状态栏显示「战场已锁定」即已生效。\n\n" +
                "【战斗操作】点一下立即生效，不需要开关：\n" +
                "  · 清空全场敌人 —— 一次性秒掉地图上所有敌人\n" +
                "  · 我方全体满血 —— 立刻把士兵和英雄治疗满\n" +
                "  · 技能立刻就绪 —— 陨石雨 / 援军 立刻结束冷却\n\n" +
                "【存档一键修改】\n" +
                "  改的是 %APPDATA%\\kingdom_rush_frontiers\\slot_1.lua。\n" +
                "  每次写入前自动备份成 slot_1.lua.bak-日期时间，可随时还原。\n" +
                "  改存档前请完全退出游戏，否则游戏退出时会把旧存档写回去。\n\n" +
                "【还原】\n" +
                "  游戏本体：点「卸载并还原游戏」\n" +
                "  存档    ：点「还原最近备份」\n\n" +
                "【常见问题】\n" +
                "  · 改完存档被还原？多半是 Steam 云同步覆盖，先退出游戏再改。\n" +
                "  · 杀毒软件报毒？重打包游戏 exe 属常见误报，可只用存档功能。";
            MessageBox.Show(this, msg, "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void Log(string msg)
        {
            if (txtLog != null) txtLog.AppendText(msg + "\r\n");
            try
            {
                Directory.CreateDirectory(Ctl.Dir);
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n", Encoding.UTF8);
            }
            catch { }
        }

        // ---------------------------------------------------------- 命令行 ----
        public static int RunCli(string[] args)
        {
            if (AttachConsole(-1))
            {
                try
                {
                    StreamWriter w = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding);
                    w.AutoFlush = true;
                    Console.SetOut(w);
                }
                catch { }
            }
            MainForm f = new MainForm();
            string cmd = args[0].ToLowerInvariant();
            string dir = f.DetectGameDir();
            if (cmd == "--selftest-save")
            {
                string sd = f.DetectSaveDir();
                string slot = Path.Combine(sd, "slot_1.lua");
                if (!File.Exists(slot)) { Console.Error.WriteLine("[错误] 找不到存档 " + slot); return 7; }
                string orig = File.ReadAllText(slot, Encoding.UTF8).Replace("\r\n", "\n");
                Dictionary<object, object> t = LuaSave.Parse(orig);
                string again = LuaSave.Serialize(t).Replace("\r\n", "\n");
                bool same = orig.TrimEnd('\n') == again.TrimEnd('\n');
                Console.WriteLine("存档: " + slot);
                Console.WriteLine("原文件 " + orig.Length + " 字符, 重新序列化 " + again.Length + " 字符");
                Console.WriteLine("逐字节一致: " + (same ? "是" : "否"));
                if (!same)
                {
                    string[] a = orig.Split('\n'), b = again.Split('\n');
                    int n = Math.Min(a.Length, b.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (a[i] != b[i])
                        {
                            Console.WriteLine("首个差异 @ 行 " + (i + 1));
                            Console.WriteLine("  原: " + a[i]);
                            Console.WriteLine("  新: " + b[i]);
                            break;
                        }
                    }
                    return 6;
                }
                Console.WriteLine("宝石=" + SaveOps.GetGems(t) + "  总星=" + SaveOps.TotalStars(t));
                return 0;
            }
            if (dir == null)
            {
                Console.Error.WriteLine("[错误] 找不到游戏安装目录");
                return 2;
            }
            string exe = Path.Combine(dir, EXE_NAME);
            string bak = exe + BACKUP_SUFFIX;
            try
            {
                if (cmd == "--status")
                {
                    Console.WriteLine("游戏目录: " + dir);
                    Console.WriteLine("修改器  : " + (File.Exists(bak) ? "已安装" : "未安装"));
                    Console.WriteLine("游戏进程: " + (f.IsRunning() ? "运行中" : "未运行"));
                    Console.WriteLine("存档目录: " + f.DetectSaveDir());
                    return 0;
                }
                if (cmd == "--install")
                {
                    if (f.IsRunning()) { Console.Error.WriteLine("[错误] 游戏正在运行，请先退出"); return 3; }
                    f.Install(dir);
                    if (!File.Exists(Ctl.File_)) Ctl.Write(Ctl.Read());
                    Console.WriteLine("[完成] 已安装到 " + dir);
                    return 0;
                }
                if (cmd == "--uninstall")
                {
                    if (f.IsRunning()) { Console.Error.WriteLine("[错误] 游戏正在运行，请先退出"); return 3; }
                    if (!File.Exists(bak)) { Console.Error.WriteLine("[错误] 没有找到备份"); return 4; }
                    string tmp = exe + ".dsh-restore";
                    File.Copy(bak, tmp, true);
                    File.Copy(tmp, exe, true);
                    File.Delete(tmp);
                    File.Delete(bak);
                    Console.WriteLine("[完成] 已还原原版");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[错误] " + ex.Message);
                return 5;
            }
            Console.Error.WriteLine("用法: KrTrainer.exe [--install | --uninstall | --status | --selftest-save]");
            return 1;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0)
            {
                Environment.ExitCode = MainForm.RunCli(args);
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
