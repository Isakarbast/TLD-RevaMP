using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Anti-aliasing as a choice. The game's one switch (Settings > Graphics > Antialiasing) turns on two methods at
    /// once: temporal AA on every post-processing layer (pplayermelle.TurnOn, from mainscript.SettingsRefreshed and
    /// pplayermelle.FStart) and 8x MSAA (settingsscript.RefreshInMain: QualitySettings.antiAliasing = 8). Both smooth the
    /// same edges; together they were the one setting that made the graphics card the limit (GPU 1.7 -> 3.0 ms a frame,
    /// PerfA 2026-11-03). Settings > Revamp > Anti-aliasing picks one: the post-processing stack's own FXAA, SMAA or TAA
    /// (with the game's TAA tuning), or MSAA 2x/4x/8x, or none. MSAA works here (every camera renders forward with
    /// allowMSAA): it smooths object outlines (a car 20 m off at 2x/4x/8x) but not where the deck meets the terrain in the
    /// distance, two nearly flat surfaces whose edge the depth test decides the same way in every sample. Most of its cost
    /// is the HDR targets: 8x with HDR off costs about a third (PerfA 2026-11-03), not an option as it changes the picture.
    /// "Game's own" (the default) leaves the game's switch in charge, exactly as before. Not in VR: VrPerf manages
    /// anti-aliasing there.
    public static class AntiAliasingChoice
    {
        public static readonly string[] Options = { "Game's own (TAA + 8x MSAA when on)", "Off", "FXAA", "SMAA", "TAA", "MSAA 2x", "MSAA 4x", "MSAA 8x" };
        public static int Mode;   // 0: the game's own
        public static long Applied;

        public static void Set(int v)
        {
            int was = Mode;
            Mode = Mathf.Clamp(v, 0, Options.Length - 1);
            if (Mode != 0) Apply();
            else if (was != 0) GameOwn();
        }

        private static int Msaa => Mode == 5 ? 2 : Mode == 6 ? 4 : Mode == 7 ? 8 : 0;

        private static bool Active => Mode != 0 && mainscript.s != null && !mainscript.IsVR();

        internal static void Apply()
        {
            if (!Active) return;
            QualitySettings.antiAliasing = Msaa;
            if (mainscript.s.pplayers != null)
                foreach (var pl in mainscript.s.pplayers) Layer(pl);
            Applied++;
        }

        // the post-processing stack is not referenced here (as in VR.Native.VrPerf): its layer and mode by reflection
        private static readonly System.Reflection.FieldInfo LayerField = AccessTools.Field(typeof(pplayermelle), "pplayer");
        private static System.Reflection.FieldInfo _modeField;

        private static void Layer(pplayermelle pl)
        {
            var layer = pl ? LayerField?.GetValue(pl) as Component : null;
            if (!layer) return;
            if (Mode == 4)
            {
                var ms = mainscript.s;
                pl.TurnOn(ms.AntialiasingJitterSpread, ms.AntialiasingStationaryBlending, ms.AntialiasingMotionBlending, ms.AntialiasingSharpness);
                return;
            }
            if (_modeField == null) _modeField = layer.GetType().GetField("antialiasingMode");
            if (_modeField == null) return;
            string name = Mode == 2 ? "FastApproximateAntialiasing" : Mode == 3 ? "SubpixelMorphologicalAntialiasing" : "None";
            _modeField.SetValue(layer, System.Enum.Parse(_modeField.FieldType, name));
        }

        /// Back to "Game's own": what the game's switch does, as its own code does it.
        private static void GameOwn()
        {
            var ms = mainscript.s;
            if (ms == null || settingsscript.s == null || settingsscript.s.S == null || mainscript.IsVR()) return;
            bool on = settingsscript.s.S.BAntialiasing;
            QualitySettings.antiAliasing = on ? 8 : 0;
            if (ms.pplayers != null)
                foreach (var pl in ms.pplayers)
                {
                    if (!pl || !(LayerField?.GetValue(pl) as Component)) continue;
                    if (on) pl.TurnOn(ms.AntialiasingJitterSpread, ms.AntialiasingStationaryBlending, ms.AntialiasingMotionBlending, ms.AntialiasingSharpness);
                    else pl.TurnOff();
                }
        }

        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SettingsRefreshed))]
        private static class AfterSettings
        {
            [HarmonyPostfix] private static void Postfix() => Apply();
        }

        [HarmonyPatch(typeof(settingsscript), "RefreshInMain")]
        private static class AfterRefreshInMain
        {
            [HarmonyPostfix] private static void Postfix() { if (Active) QualitySettings.antiAliasing = Msaa; }
        }

        [HarmonyPatch(typeof(pplayermelle), nameof(pplayermelle.FStart))]
        private static class NewLayer
        {
            [HarmonyPostfix] private static void Postfix(pplayermelle __instance) { if (Active) Layer(__instance); }
        }

        internal static string Stats() =>
            "{\"mode\":" + Mode + ",\"name\":" + Json.Str(Options[Mode]) + ",\"msaa\":" + QualitySettings.antiAliasing + ",\"applied\":" + Applied + "}";
    }
}
