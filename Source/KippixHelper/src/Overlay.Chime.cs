using System;
using System.IO;

namespace KippixOverlay
{
    /// <summary>
    /// (0.99.30) The overlay's "screenshot saved" sound, played with Windows' own player so it never waits on the game or the theme.
    /// (0.99.31) The theme's Audio\OverlayScreenshot.wav; without it, two soft bell notes going up (G5 then C6) made here.
    /// </summary>
    internal static class Chime
    {
        private static byte[] wav;
        private static System.Media.SoundPlayer player;

        /// <summary>(0.99.31) The theme's own sound for it (Audio\OverlayScreenshot.wav, the user's choice: a soft "pop"); the made-up chime when it's missing.</summary>
        public static string SoundFile;

        public static void Play()
        {
            try
            {
                if (player == null)
                {
                    string f = SoundFile;
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) player = new System.Media.SoundPlayer(new MemoryStream(File.ReadAllBytes(f)));
                    else { wav = Make(); player = new System.Media.SoundPlayer(new MemoryStream(wav)); }
                    player.Load();
                }
                player.Play();   // (async: returns at once)
            }
            catch (Exception) { }
        }

        /// <summary>(0.99.37) The overlay opening / closing: the theme's Audio\OverlayOpen.wav (a light whoosh-pop), nothing without it.</summary>
        public static string OpenFile;
        private static System.Media.SoundPlayer openPlayer;
        private static bool openMissing;

        public static void PlayOpen()
        {
            try
            {
                if (openPlayer == null)
                {
                    if (openMissing) return;
                    string f = OpenFile;
                    if (string.IsNullOrEmpty(f) || !File.Exists(f)) { openMissing = true; return; }
                    openPlayer = new System.Media.SoundPlayer(new MemoryStream(File.ReadAllBytes(f)));
                    openPlayer.Load();
                }
                openPlayer.Play();
            }
            catch (Exception) { }
        }

        private static byte[] Make()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.62);
            var pcm = new short[n];
            AddNote(pcm, rate, 783.99, 0.00, 0.26);    // G5
            AddNote(pcm, rate, 1046.50, 0.095, 0.30);  // C6, a moment later
            int fade = rate / 30;   // (the last 33 ms go to silence: no click at the end)
            for (int i = 0; i < fade; i++) { int k = n - fade + i; pcm[k] = (short)(pcm[k] * (1.0 - i / (double)fade)); }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
            foreach (short s in pcm) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>A bell-like note: the tone with a little of its 2nd and 3rd harmonics, a 6 ms start and a soft fade.</summary>
        private static void AddNote(short[] pcm, int rate, double freq, double start, double level)
        {
            int from = (int)(start * rate);
            for (int i = from; i < pcm.Length; i++)
            {
                double t = (i - from) / (double)rate;
                double env = Math.Min(1.0, t / 0.006) * Math.Exp(-t / 0.17);
                double v = Math.Sin(2 * Math.PI * freq * t) + 0.22 * Math.Sin(4 * Math.PI * freq * t) * Math.Exp(-t / 0.08) + 0.06 * Math.Sin(6 * Math.PI * freq * t) * Math.Exp(-t / 0.05);
                double s = pcm[i] + 32767.0 * level * env * v / 1.28;
                pcm[i] = (short)Math.Max(-32767, Math.Min(32767, s));
            }
        }
    }
}
