using System;
using UnityEngine;

namespace TvBrowser
{
    /// A TV's sound source: a child of the screen, so the TV's own sounds (its button) keep theirs.
    internal sealed class TvSound : MonoBehaviour
    {
        private static AudioClip _one;
        internal AudioSource Src;
        internal volatile Sound.Buffer Buffer;
        internal bool Quiet;   // muted by Probe.Quiet (tests)
        private int _outRate;

        public static TvSound Make(Transform screen)
        {
            var go = new GameObject("TvBrowserSound");
            go.transform.SetParent(screen, false);
            var src = go.AddComponent<AudioSource>();
            src.outputAudioMixerGroup = mainscript.s != null ? mainscript.s.defGroup : null;
            src.spatialBlend = 1f; src.dopplerLevel = 0f; src.priority = 32;
            src.rolloffMode = AudioRolloffMode.Linear; src.minDistance = 2f;
            src.loop = true;
            int rate = AudioSettings.outputSampleRate;
            if (_one == null)
            {
                _one = AudioClip.Create("TvBrowserCarrier", rate, 1, rate, false);
                var ones = new float[rate]; for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
                _one.SetData(ones, 0);
            }
            src.clip = _one;
            var s = go.AddComponent<TvSound>();
            s.Src = src; s._outRate = rate;
            s.Settings();
            src.Play();
            return s;
        }

        /// The player's settings (Plugin: Sound).
        public void Settings()
        {
            Src.volume = Plugin.Volume.Value;
            Src.maxDistance = Mathf.Max(Src.minDistance + 1f, Plugin.HearWithin.Value);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            var b = Buffer;
            if (b == null || _outRate <= 0) { Array.Clear(data, 0, data.Length); return; }
            Sound.Fill(b, data, channels, _outRate);
        }
    }
}
