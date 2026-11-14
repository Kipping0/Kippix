using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KippixHelper
{
    /// <summary>
    /// A short menu sound played by Windows itself (winmm PlaySound from memory): it starts at once, every time. WPF's
    /// MediaPlayer, rewound and played for each move, sometimes stayed silent on quick moves. The volume is applied to the
    /// samples (a copy is made per volume). Windows plays one such sound at a time: a new move cuts the last one short,
    /// like a console menu. Only plain PCM WAV files (8 / 16 bit); anything else returns null and MediaPlayer is used.
    /// </summary>
    internal sealed class WavSound : IDisposable
    {
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

        private readonly byte[] file;
        private readonly int dataOffset, dataLength, bits;
        private IntPtr mem = IntPtr.Zero;
        private int memVolume = -1;

        private WavSound(byte[] file, int dataOffset, int dataLength, int bits)
        {
            this.file = file;
            this.dataOffset = dataOffset;
            this.dataLength = dataLength;
            this.bits = bits;
        }

        public static WavSound Load(string path)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return null;
                int pos = 12, fmtTag = 0, bitsPer = 0, dataAt = -1, dataLen = 0;
                while (pos + 8 <= b.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                    int len = BitConverter.ToInt32(b, pos + 4);
                    if (len < 0) return null;
                    if (id == "fmt " && pos + 8 + 16 <= b.Length)
                    {
                        fmtTag = BitConverter.ToInt16(b, pos + 8);
                        bitsPer = BitConverter.ToInt16(b, pos + 8 + 14);
                    }
                    else if (id == "data")
                    {
                        dataAt = pos + 8;
                        if (len > b.Length - dataAt) return null;   // cut short: Windows would read past its end
                        dataLen = len;
                        break;
                    }
                    pos += 8 + len + (len & 1);
                }
                if (fmtTag != 1 || (bitsPer != 8 && bitsPer != 16) || dataAt < 0) return null;
                return new WavSound(b, dataAt, dataLen, bitsPer);
            }
            catch (Exception) { return null; }
        }

        /// <param name="volume">0-100</param>
        public void Play(int volume)
        {
            volume = Math.Max(0, Math.Min(100, volume));
            if (volume == 0) return;
            if (volume != memVolume) Build(volume);
            if (mem != IntPtr.Zero) PlaySound(mem, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }

        private void Build(int volume)
        {
            Free();
            var copy = (byte[])file.Clone();
            Array.Copy(BitConverter.GetBytes(copy.Length - 8), 0, copy, 4, 4);   // the RIFF size Windows trusts: exactly what's here
            double f = volume / 100.0;
            if (volume < 100)
            {
                if (bits == 16)
                    for (int i = dataOffset; i + 1 < dataOffset + dataLength; i += 2)
                    {
                        int v = (short)(copy[i] | (copy[i + 1] << 8));
                        v = (int)Math.Round(v * f);
                        copy[i] = (byte)(v & 0xFF);
                        copy[i + 1] = (byte)((v >> 8) & 0xFF);
                    }
                else
                    for (int i = dataOffset; i < dataOffset + dataLength; i++)
                        copy[i] = (byte)Math.Max(0, Math.Min(255, 128 + (int)Math.Round((copy[i] - 128) * f)));
            }
            mem = Marshal.AllocHGlobal(copy.Length);
            Marshal.Copy(copy, 0, mem, copy.Length);
            memVolume = volume;
        }

        private void Free()
        {
            if (mem == IntPtr.Zero) return;
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);   // stop first: Windows may still be reading it
            Marshal.FreeHGlobal(mem);
            mem = IntPtr.Zero;
            memVolume = -1;
        }

        public void Dispose() { Free(); }
    }
}
