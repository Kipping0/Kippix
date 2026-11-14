using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KippixHelper
{
    /// <summary>
    /// Turns fragmented MP4 streams (DASH pieces joined together: one video file, one audio file) into one ordinary
    /// MP4 with a full sample index, so Windows' player can seek in it and the sound stays in step with the picture.
    /// Only rewrites the boxes; the audio and video data are copied as they are.
    /// </summary>
    internal static class Mp4Remux
    {
        private class Sample
        {
            public long Offset;   // in the source file
            public int Size;
            public uint Duration;
            public int Cto;       // composition time offset
            public bool Sync;
        }

        private class Track
        {
            public byte[] Data;
            public byte[] Hdlr, Stsd, MediaHeaderBox, Dinf;   // copied as they are
            public string Handler;   // "vide" or "soun"
            public uint Timescale;
            public ushort Language;
            public byte[] WidthHeight = new byte[8];   // 16.16 fixed width / height from tkhd
            public long EditMediaTime = -1;            // from the source edit list
            public List<Sample> Samples = new List<Sample>();
            public ulong Duration;                     // in Timescale units
            public long OutOffset;                     // where this track's data starts in the output
        }

        private const uint MovieTimescale = 1000;

        /// <summary>Writes <paramref name="output"/> from the video stream and (optionally) the audio stream. Throws if a file isn't what's expected.</summary>
        public static void Remux(string videoFile, string audioFile, string output)
        {
            var tracks = new List<Track>();
            tracks.Add(ReadTrack(videoFile));
            if (audioFile != null && File.Exists(audioFile))
            {
                try { tracks.Add(ReadTrack(audioFile)); } catch (Exception) { }   // play without sound rather than not at all
            }
            foreach (var t in tracks) if (t.Samples.Count == 0) throw new InvalidDataException("no samples");

            byte[] ftyp = Box("ftyp", Concat(Ascii("isom"), U32(512), Ascii("isom"), Ascii("iso2"), Ascii("avc1"), Ascii("mp41")));
            // moov's size doesn't depend on the offsets (64-bit chunk offsets), so build it once to measure, then again for real
            byte[] moov = Moov(tracks);
            long pos = ftyp.Length + moov.Length + 16;   // 16 = large mdat header
            foreach (var t in tracks)
            {
                t.OutOffset = pos;
                foreach (var s in t.Samples) pos += s.Size;
            }
            moov = Moov(tracks);
            long mdatSize = pos - ftyp.Length - moov.Length;

            string tmp = output + ".part";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(ftyp, 0, ftyp.Length);
                fs.Write(moov, 0, moov.Length);
                byte[] head = Concat(U32(1), Ascii("mdat"), U64((ulong)mdatSize));
                fs.Write(head, 0, head.Length);
                foreach (var t in tracks)
                    foreach (var s in t.Samples) fs.Write(t.Data, (int)s.Offset, s.Size);
            }
            if (File.Exists(output)) File.Delete(output);
            File.Move(tmp, output);
        }

        // ---------------- reading ----------------

        private static Track ReadTrack(string file)
        {
            var t = new Track { Data = File.ReadAllBytes(file) };
            byte[] b = t.Data;
            uint defDuration = 0, defSize = 0, defFlags = 0;
            bool haveMoov = false;
            long o = 0;
            while (o + 8 <= b.Length)
            {
                long size; int header; string type;
                if (!BoxHeader(b, o, b.Length, out size, out header, out type)) break;
                if (type == "moov" && !haveMoov)
                {
                    haveMoov = true;
                    ReadMoov(t, b, o + header, o + size, ref defDuration, ref defSize, ref defFlags);
                }
                else if (type == "moof")
                {
                    if (!haveMoov) throw new InvalidDataException("moof before moov");
                    ReadMoof(t, b, o, o + header, o + size, defDuration, defSize, defFlags);
                }
                o += size;
            }
            if (!haveMoov || t.Stsd == null || t.Timescale == 0) throw new InvalidDataException("not an MP4 stream");
            ulong total = 0;
            foreach (var s in t.Samples) total += s.Duration;
            t.Duration = total;
            return t;
        }

        private static void ReadMoov(Track t, byte[] b, long start, long end, ref uint defDuration, ref uint defSize, ref uint defFlags)
        {
            foreach (var box in Children(b, start, end))
            {
                if (box.Type == "trak") ReadTrak(t, b, box.Body, box.End);
                else if (box.Type == "mvex")
                {
                    foreach (var x in Children(b, box.Body, box.End))
                        if (x.Type == "trex")
                        {
                            // version/flags, track_ID, default_sample_description_index, duration, size, flags
                            defDuration = R32(b, x.Body + 12);
                            defSize = R32(b, x.Body + 16);
                            defFlags = R32(b, x.Body + 20);
                        }
                }
            }
        }

        private static void ReadTrak(Track t, byte[] b, long start, long end)
        {
            foreach (var box in Children(b, start, end))
            {
                if (box.Type == "tkhd") Array.Copy(b, box.End - 8, t.WidthHeight, 0, 8);
                else if (box.Type == "edts")
                {
                    foreach (var e in Children(b, box.Body, box.End))
                        if (e.Type == "elst" && R32(b, e.Body + 4) > 0)
                        {
                            bool v1 = b[e.Body] == 1;
                            long mt = v1 ? (long)R64(b, e.Body + 8 + 8) : (int)R32(b, e.Body + 8 + 4);
                            if (mt >= 0) t.EditMediaTime = mt;
                        }
                }
                else if (box.Type == "mdia")
                {
                    foreach (var m in Children(b, box.Body, box.End))
                    {
                        if (m.Type == "mdhd")
                        {
                            bool v1 = b[m.Body] == 1;
                            t.Timescale = R32(b, m.Body + (v1 ? 20 : 12));
                            t.Language = (ushort)((b[m.Body + (v1 ? 32 : 20)] << 8) | b[m.Body + (v1 ? 33 : 21)]);
                        }
                        else if (m.Type == "hdlr")
                        {
                            t.Hdlr = Slice(b, m.Start, m.End);
                            t.Handler = Encoding.ASCII.GetString(b, (int)m.Body + 8, 4);
                        }
                        else if (m.Type == "minf")
                        {
                            foreach (var n in Children(b, m.Body, m.End))
                            {
                                if (n.Type == "vmhd" || n.Type == "smhd") t.MediaHeaderBox = Slice(b, n.Start, n.End);
                                else if (n.Type == "dinf") t.Dinf = Slice(b, n.Start, n.End);
                                else if (n.Type == "stbl")
                                    foreach (var s in Children(b, n.Body, n.End))
                                        if (s.Type == "stsd") t.Stsd = Slice(b, s.Start, s.End);
                            }
                        }
                    }
                }
            }
        }

        private static void ReadMoof(Track t, byte[] b, long moofStart, long start, long end, uint defDuration, uint defSize, uint defFlags)
        {
            foreach (var traf in Children(b, start, end))
            {
                if (traf.Type != "traf") continue;
                long baseOffset = moofStart;
                uint duration = defDuration, size = defSize, flags = defFlags;
                long dataPos = -1;
                foreach (var x in Children(b, traf.Body, traf.End))
                {
                    if (x.Type == "tfhd")
                    {
                        uint tf = R32(b, x.Body) & 0xFFFFFF;
                        long p = x.Body + 8;   // after version/flags and track_ID
                        if ((tf & 0x1) != 0) { baseOffset = (long)R64(b, p); p += 8; }
                        if ((tf & 0x2) != 0) p += 4;
                        if ((tf & 0x8) != 0) { duration = R32(b, p); p += 4; }
                        if ((tf & 0x10) != 0) { size = R32(b, p); p += 4; }
                        if ((tf & 0x20) != 0) { flags = R32(b, p); p += 4; }
                    }
                    else if (x.Type == "trun")
                    {
                        uint word = R32(b, x.Body);
                        int version = (int)(word >> 24);
                        uint rf = word & 0xFFFFFF;
                        uint count = R32(b, x.Body + 4);
                        long p = x.Body + 8;
                        if ((rf & 0x1) != 0) { dataPos = baseOffset + (int)R32(b, p); p += 4; }
                        else if (dataPos < 0) dataPos = baseOffset;
                        uint firstFlags = flags;
                        bool hasFirst = (rf & 0x4) != 0;
                        if (hasFirst) { firstFlags = R32(b, p); p += 4; }
                        for (uint i = 0; i < count; i++)
                        {
                            uint d = duration, sz = size, f = (i == 0 && hasFirst) ? firstFlags : flags;
                            int cto = 0;
                            if ((rf & 0x100) != 0) { d = R32(b, p); p += 4; }
                            if ((rf & 0x200) != 0) { sz = R32(b, p); p += 4; }
                            if ((rf & 0x400) != 0) { f = R32(b, p); p += 4; if (i == 0 && hasFirst) f = firstFlags; }
                            if ((rf & 0x800) != 0) { uint c = R32(b, p); p += 4; cto = version == 0 ? (int)Math.Min(c, (uint)int.MaxValue) : (int)c; }
                            if (dataPos < 0 || dataPos + sz > b.Length) throw new InvalidDataException("sample outside the file");
                            t.Samples.Add(new Sample { Offset = dataPos, Size = (int)sz, Duration = d, Cto = cto, Sync = (f & 0x10000) == 0 });
                            dataPos += sz;
                        }
                    }
                }
            }
        }

        // ---------------- writing ----------------

        private static byte[] Moov(List<Track> tracks)
        {
            ulong movieDuration = 0;
            foreach (var t in tracks) movieDuration = Math.Max(movieDuration, ToMovie(t.Duration, t.Timescale));
            var parts = new List<byte[]>();
            parts.Add(FullBox("mvhd", 0, 0, Concat(U32(0), U32(0), U32(MovieTimescale), U32((uint)movieDuration),
                U32(0x00010000), U16(0x0100), new byte[10], Matrix(), new byte[24], U32((uint)tracks.Count + 1))));
            for (int i = 0; i < tracks.Count; i++) parts.Add(Trak(tracks[i], (uint)(i + 1)));
            return Box("moov", Concat(parts.ToArray()));
        }

        private static byte[] Trak(Track t, uint id)
        {
            bool audio = t.Handler == "soun";
            ulong movieDur = ToMovie(t.Duration, t.Timescale);
            byte[] tkhd = FullBox("tkhd", 0, 3, Concat(U32(0), U32(0), U32(id), U32(0), U32((uint)movieDur), new byte[8],
                U16(0), U16(0), U16(audio ? (ushort)0x0100 : (ushort)0), U16(0), Matrix(), t.WidthHeight));

            // edit list: start at the source's media time (or the first frame's composition offset), as players expect
            long mediaTime = t.EditMediaTime >= 0 ? t.EditMediaTime : (t.Samples.Count > 0 && t.Samples[0].Cto > 0 ? t.Samples[0].Cto : 0);
            byte[] edts = Box("edts", FullBox("elst", 0, 0, Concat(U32(1), U32((uint)movieDur), U32((uint)mediaTime), U16(1), U16(0))));

            byte[] mdhd = FullBox("mdhd", 1, 0, Concat(U64(0), U64(0), U32(t.Timescale), U64(t.Duration), U16(t.Language), U16(0)));
            byte[] minf = Box("minf", Concat(t.MediaHeaderBox ?? (audio ? FullBox("smhd", 0, 0, new byte[4]) : FullBox("vmhd", 0, 1, new byte[8])),
                t.Dinf ?? Box("dinf", FullBox("dref", 0, 0, Concat(U32(1), FullBox("url ", 0, 1, new byte[0])))),
                Stbl(t)));
            byte[] mdia = Box("mdia", Concat(mdhd, t.Hdlr, minf));
            return Box("trak", Concat(tkhd, edts, mdia));
        }

        private static byte[] Stbl(Track t)
        {
            var s = t.Samples;
            // stts: runs of equal durations
            var stts = new List<byte[]>();
            int runs = 0;
            for (int i = 0; i < s.Count; )
            {
                int j = i;
                while (j < s.Count && s[j].Duration == s[i].Duration) j++;
                stts.Add(Concat(U32((uint)(j - i)), U32(s[i].Duration)));
                runs++;
                i = j;
            }
            var parts = new List<byte[]>();
            parts.Add(t.Stsd);
            parts.Add(FullBox("stts", 0, 0, Concat(U32((uint)runs), Concat(stts.ToArray()))));

            bool anyCto = false, negative = false;
            foreach (var x in s) { if (x.Cto != 0) anyCto = true; if (x.Cto < 0) negative = true; }
            if (anyCto)
            {
                var ctts = new List<byte[]>();
                int n = 0;
                for (int i = 0; i < s.Count; )
                {
                    int j = i;
                    while (j < s.Count && s[j].Cto == s[i].Cto) j++;
                    ctts.Add(Concat(U32((uint)(j - i)), U32((uint)s[i].Cto)));
                    n++;
                    i = j;
                }
                parts.Add(FullBox("ctts", (byte)(negative ? 1 : 0), 0, Concat(U32((uint)n), Concat(ctts.ToArray()))));
            }

            bool allSync = true;
            foreach (var x in s) if (!x.Sync) { allSync = false; break; }
            if (!allSync)
            {
                var keys = new List<byte[]>();
                for (int i = 0; i < s.Count; i++) if (s[i].Sync) keys.Add(U32((uint)(i + 1)));
                parts.Add(FullBox("stss", 0, 0, Concat(U32((uint)keys.Count), Concat(keys.ToArray()))));
            }

            // one sample per chunk keeps it simple: every sample gets its own 64-bit offset
            parts.Add(FullBox("stsc", 0, 0, Concat(U32(1), U32(1), U32(1), U32(1))));
            var sizes = new byte[s.Count * 4];
            var offsets = new byte[s.Count * 8];
            long pos = t.OutOffset;
            for (int i = 0; i < s.Count; i++)
            {
                W32(sizes, i * 4, (uint)s[i].Size);
                W64(offsets, i * 8, (ulong)pos);
                pos += s[i].Size;
            }
            parts.Add(FullBox("stsz", 0, 0, Concat(U32(0), U32((uint)s.Count), sizes)));
            parts.Add(FullBox("co64", 0, 0, Concat(U32((uint)s.Count), offsets)));
            return Box("stbl", Concat(parts.ToArray()));
        }

        private static ulong ToMovie(ulong duration, uint timescale)
        {
            return timescale == 0 ? 0 : duration * MovieTimescale / timescale;
        }

        // ---------------- box helpers ----------------

        private class BoxRef { public string Type; public long Start, Body, End; }

        private static IEnumerable<BoxRef> Children(byte[] b, long start, long end)
        {
            long o = start;
            while (o + 8 <= end)
            {
                long size; int header; string type;
                if (!BoxHeader(b, o, end, out size, out header, out type)) yield break;
                yield return new BoxRef { Type = type, Start = o, Body = o + header, End = o + size };
                o += size;
            }
        }

        private static bool BoxHeader(byte[] b, long o, long end, out long size, out int header, out string type)
        {
            size = R32(b, o);
            header = 8;
            type = Encoding.ASCII.GetString(b, (int)o + 4, 4);
            if (size == 1) { size = (long)R64(b, o + 8); header = 16; }
            else if (size == 0) size = end - o;
            return size >= header && o + size <= end;
        }

        private static byte[] Box(string type, byte[] body)
        {
            return Concat(U32((uint)(body.Length + 8)), Ascii(type), body);
        }

        private static byte[] FullBox(string type, byte version, uint flags, byte[] body)
        {
            return Box(type, Concat(U32(((uint)version << 24) | (flags & 0xFFFFFF)), body));
        }

        private static byte[] Matrix()
        {
            return Concat(U32(0x00010000), U32(0), U32(0), U32(0), U32(0x00010000), U32(0), U32(0), U32(0), U32(0x40000000));
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        private static byte[] Slice(byte[] b, long start, long end)
        {
            var r = new byte[end - start];
            Array.Copy(b, start, r, 0, r.Length);
            return r;
        }

        private static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }
        private static byte[] U16(ushort v) { return new[] { (byte)(v >> 8), (byte)v }; }
        private static byte[] U32(uint v) { var r = new byte[4]; W32(r, 0, v); return r; }
        private static byte[] U64(ulong v) { var r = new byte[8]; W64(r, 0, v); return r; }
        private static void W32(byte[] r, int o, uint v) { r[o] = (byte)(v >> 24); r[o + 1] = (byte)(v >> 16); r[o + 2] = (byte)(v >> 8); r[o + 3] = (byte)v; }
        private static void W64(byte[] r, int o, ulong v) { W32(r, o, (uint)(v >> 32)); W32(r, o + 4, (uint)v); }
        private static uint R32(byte[] b, long o) { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }
        private static ulong R64(byte[] b, long o) { return ((ulong)R32(b, o) << 32) | R32(b, o + 4); }
    }
}
