using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Clouds in a session, on the world clock. The sky's own code (napszakvaltakozas.FixedUpdate) makes the cloud cover
    /// from the time of day, the biome under the player and a slow wobble: CloudPerlin, a timer each machine starts at
    /// load and runs on its own frame time (faster by sleepModifier while that player sleeps). The time of day is the
    /// host's (Net.Entities.Clock), the wobble wasn't: a player who joined later had another cloud cover. And while one
    /// player slept, only their clouds raced (Felho: the scroll speed's curve over the sleep).
    ///
    ///  - The wobble: WorldTotal is the world time gone by in the session, days and sleeps included (the game keeps no
    ///    day count). The host's goes out with every world clock (2 s, and with each live clock of a sleeper); here it
    ///    follows this machine's own time of day between them. CloudPerlin is set from it each step
    ///    (CloudClockMath.Phase): the game's own timer went with the world time too (sleepModifierDef is the sky's
    ///    speed-up while asleep), so the clouds look and move as before, at the same place on every machine.
    ///  - The sleep's scroll speed: the sleeper's live clock (SleepSync) carries where their curve stands, the world
    ///    clock carries it on, and every other machine runs the same curve (CloudClockMath.SleepCurve between reports).
    /// Only in a session: alone, the game's own timer is the world's. The host's clouds stay where they were when it
    /// starts hosting; a joiner's move once, with the time of day.
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.CloudClock.Enabled (the wobble), .SleepSpeed (the scroll speed).
    public static class CloudClock
    {
        public static bool Enabled = true, SleepSpeed = true;
        public static long Steps, HostTotals, SleepCurves, SleepCurvesEnded;
        public static double WorldTotal;
        public static float SleepCurveShown = -1f;

        private static readonly AccessTools.FieldRef<napszakvaltakozas, float> Perlin = AccessTools.FieldRefAccess<napszakvaltakozas, float>("CloudPerlin");
        private static float _lastT = -1f;
        private static bool _hostPending, _curve;
        private static double _hostTotal, _hostPosition, _hostStamp, _hostRate;
        private static float _sleepProgress = -1f;
        private static double _sleepAt, _sleepRate = double.NaN;

        /// A client: the host's total from a world clock (Net.Entities.ReceiveWorldClock), taken at the next step.
        internal static void HostTotal(double total, double position, double stamp, double rate)
        {
            _hostTotal = total; _hostPosition = position; _hostStamp = stamp; _hostRate = rate; _hostPending = true;
        }

        /// A sleeping player's cloud curve (0..1), or -1: nobody sleeps (any more). The periodic world clock repeats the
        /// last report: a repeat is no news (taken as one, the curve would step back to it).
        internal static void SleepProgress(float progress)
        {
            float p = progress >= 0f ? Mathf.Clamp01(progress) : -1f;
            if (p == _sleepProgress) return;
            double now = Time.realtimeSinceStartupAsDouble;
            float real = mainscript.s != null ? mainscript.s.sleepRealTime : 0f;
            _sleepRate = p < 0f ? double.NaN : CloudClockMath.SleepRate(_sleepProgress, p, now - _sleepAt, real);
            _sleepProgress = p; _sleepAt = now;
        }

        /// This machine's own sleep's curve, as the game runs it (mainscript: num2), for its live clock.
        internal static float MySleepProgress()
        {
            var ms = mainscript.s;
            if (ms == null || !ms.sleeping) return -1f;
            return Mathf.InverseLerp(ms.sleepEndTime - ms.sleepRealTime * 2f, ms.sleepEndTime, Time.time);
        }

        [HarmonyPatch(typeof(napszakvaltakozas), "FixedUpdate")]
        private static class Follow
        {
            [HarmonyPrefix, HarmonyPriority(Priority.Low)]   // after Entities.WorldFollowsHost has set this step's clock
            private static void Prefix(napszakvaltakozas __instance)
            {
                var ms = mainscript.s;
                if (!Net.Entities.InSession || ms == null) { Stop(__instance); return; }
                double length = __instance.dt + __instance.nt;
                float speed = __instance.CloudPerlinSpeed;
                if (length <= 0) return;
                if (_lastT < 0f) WorldTotal = speed > 0 ? CloudClockMath.TotalFor(Perlin(__instance), speed) : 0;   // where the clouds are now
                else WorldTotal += Net.ClockMath.Difference(__instance.t, _lastT, length);
                if (_hostPending && !ms.sleeping)   // our own sleep runs the clock here; the host's next one has it
                {
                    WorldTotal = CloudClockMath.HostTotalNow(_hostTotal, _hostPosition, _hostStamp, _hostRate, Net.ModObjects.ClockTime, __instance.t, length);
                    _hostPending = false; HostTotals++;
                }
                _lastT = __instance.t;
                if (Enabled && speed > 0)
                {
                    // the game adds this step's advance to CloudPerlin before it reads it: start that far behind
                    Perlin(__instance) = (float)CloudClockMath.Phase(WorldTotal, speed) - Time.deltaTime * speed * ms.sleepModifier;
                    Steps++;
                }

                if (!SleepSpeed || ms.sleeping) { _curve = false; SleepCurveShown = -1f; return; }   // our own sleep: the game's curve
                double since = Time.realtimeSinceStartupAsDouble - _sleepAt;
                if (_sleepProgress >= 0f && since < 5)
                {
                    double rate = double.IsNaN(_sleepRate) ? (ms.sleepRealTime > 0 ? 1.0 / ms.sleepRealTime : 1.0) : _sleepRate;
                    SleepCurveShown = (float)CloudClockMath.SleepCurve(_sleepProgress, since, rate);
                    __instance.Felho(SleepCurveShown);
                    if (!_curve) { _curve = true; SleepCurves++; }
                }
                else if (_curve) EndCurve(__instance);
            }
        }

        private static void EndCurve(napszakvaltakozas sw)
        {
            _curve = false; _sleepProgress = -1f; _sleepRate = double.NaN; SleepCurveShown = -1f; SleepCurvesEnded++;
            sw.Felho(1f);   // the game's speed when awake
        }

        private static void Stop(napszakvaltakozas sw)
        {
            if (_curve) EndCurve(sw);
            _sleepProgress = -1f; _sleepRate = double.NaN; _lastT = -1f; _hostPending = false;
        }

        internal static string Stats()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"sleepSpeed\":" + (SleepSpeed ? "true" : "false") +
                   ",\"worldTotal\":" + WorldTotal.ToString("F2", ci) + ",\"steps\":" + Steps + ",\"hostTotals\":" + HostTotals +
                   ",\"sleepCurves\":" + SleepCurves + ",\"sleepCurvesEnded\":" + SleepCurvesEnded + ",\"curve\":" + SleepCurveShown.ToString("F3", ci) + "}";
        }
    }
}
