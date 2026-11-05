using System;
using System.Collections.Generic;

namespace TvBrowser
{
    /// A page's sound, played from its TV. The browser plays nothing itself: tvbrowser-host gets each page's sound from
    /// CEF and sends it on its connection ("audio <key> <rate> <base64>", 16-bit stereo), read on that connection's
    /// thread straight into the page's buffer here. A DSP filter on the TV's sound source takes it out, as TLD Revamp
    /// does its voices: the source plays a looping clip of 1.0 and the filter multiplies it by the sound, so the game's
    /// volume, 3D panning and rolloff apply.
    internal static class Sound
    {
        public const int Capacity = 48000;       // frames: 1 s at 48 kHz
        public const float PrimeS = 0.1f;        // buffered before playing (the lines come about every 20 ms)
        public const float MaxLagS = 0.4f;       // more than this behind (the game paused its sound): the rest is skipped

        internal sealed class Buffer
        {
            public readonly float[] Ring = new float[Capacity * 2];   // interleaved left, right
            public int Wp, Rp, Count, Rate = 48000;
            public bool Primed, Ended;
            public double Frac;
            public long Received, Played, Dropped, Underruns;
            public volatile float Level;   // the loudest sample of the page's sound in the TV's last block (0..1)
            public volatile float Out;     // the loudest the TV's source put out in it (after its volume and rolloff)
        }

        private static readonly Dictionary<string, Buffer> Buffers = new Dictionary<string, Buffer>();

        /// A page's buffer (made if it has none yet).
        public static Buffer For(string key)
        {
            lock (Buffers)
            {
                if (!Buffers.TryGetValue(key, out var b)) Buffers[key] = b = new Buffer();
                return b;
            }
        }

        public static void Forget(string key) { if (key != null) lock (Buffers) Buffers.Remove(key); }
        public static void Clear() { lock (Buffers) Buffers.Clear(); }

        /// tvbrowser-host's "audio" and "audioend" lines (its connection's thread).
        public static void Line(string line)
        {
            var parts = line.Split(new[] { ' ' }, 4);
            if (parts.Length < 2) return;
            var b = For(parts[1]);
            if (parts[0] == "audioend") { lock (b) b.Ended = true; return; }
            if (parts.Length < 4 || !int.TryParse(parts[2], out int rate) || rate < 8000 || rate > 192000) return;
            byte[] pcm;
            try { pcm = Convert.FromBase64String(parts[3]); } catch (FormatException) { return; }
            int frames = pcm.Length / 4;
            lock (b)
            {
                if (b.Rate != rate) { b.Rate = rate; b.Count = 0; b.Rp = b.Wp; b.Primed = false; }
                b.Ended = false;
                for (int i = 0; i < frames; i++)
                {
                    b.Ring[b.Wp * 2] = (short)(pcm[4 * i] | pcm[4 * i + 1] << 8) / 32768f;
                    b.Ring[b.Wp * 2 + 1] = (short)(pcm[4 * i + 2] | pcm[4 * i + 3] << 8) / 32768f;
                    b.Wp = (b.Wp + 1) % Capacity;
                    if (b.Count < Capacity) b.Count++; else b.Rp = (b.Rp + 1) % Capacity;
                }
                b.Received += frames;
                if (b.Count > rate * MaxLagS)
                {
                    int drop = b.Count - (int)(rate * PrimeS);
                    b.Rp = (b.Rp + drop) % Capacity; b.Count -= drop; b.Dropped += drop;
                }
            }
        }

        /// One DSP block of the TV's source (the audio thread): the source's 1.0s times the page's sound, resampled
        /// (linearly) from the page's rate to the game's.
        public static void Fill(Buffer b, float[] data, int channels, int outRate)
        {
            int frames = data.Length / channels;
            float peak = 0f, outPeak = 0f;
            lock (b)
            {
                if (!b.Primed && (b.Count >= b.Rate * PrimeS || b.Ended && b.Count > 1)) b.Primed = true;
                double step = (double)b.Rate / outRate;
                int f = 0;
                for (; b.Primed && f < frames; f++)
                {
                    if (b.Count < 2) { b.Primed = false; if (!b.Ended) b.Underruns++; break; }
                    int n = (b.Rp + 1) % Capacity;
                    float t = (float)b.Frac;
                    float l = b.Ring[b.Rp * 2] + (b.Ring[n * 2] - b.Ring[b.Rp * 2]) * t;
                    float r = b.Ring[b.Rp * 2 + 1] + (b.Ring[n * 2 + 1] - b.Ring[b.Rp * 2 + 1]) * t;
                    int at = f * channels;
                    if (channels == 1) data[at] *= (l + r) * 0.5f;
                    else
                    {
                        data[at] *= l; data[at + 1] *= r;
                        for (int c = 2; c < channels; c++) data[at + c] *= (l + r) * 0.5f;
                    }
                    float a = Math.Max(Math.Abs(l), Math.Abs(r));
                    if (a > peak) peak = a;
                    for (int c = 0; c < channels; c++) if (Math.Abs(data[at + c]) > outPeak) outPeak = Math.Abs(data[at + c]);
                    b.Frac += step;
                    while (b.Frac >= 1.0 && b.Count > 1) { b.Frac -= 1.0; b.Rp = n; n = (n + 1) % Capacity; b.Count--; }
                }
                b.Played += f;
                if (f < frames) Array.Clear(data, f * channels, (frames - f) * channels);
            }
            b.Level = peak; b.Out = outPeak;
        }
    }
}
