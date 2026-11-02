using System;
using TLDRevamp.Net;

namespace TLDRevamp.Fixes
{
    /// The arithmetic of CloudClock, free of Unity (tools/unit/CloudClockTest).
    internal static class CloudClockMath
    {
        /// The cloud timer (napszakvaltakozas.CloudPerlin, kept in (-1, 0] by the game) after `total` world seconds at the
        /// game's speed. On the world's running total, not the time of day: the game's speed (0.0003) is under half a loop
        /// a day, so a timer on the time of day would jump at midnight or run faster than the game's.
        internal static double Phase(double total, double speed)
        {
            if (speed <= 0) return 0;
            double x = total * speed;
            return x - Math.Ceiling(x);
        }

        /// The total that puts the timer where it is now (the host's clouds don't move when it starts hosting).
        internal static double TotalFor(double phase, double speed) => speed > 0 ? phase / speed : 0;

        /// The host's total as of `now` here, from its world clock (total and time of day at `stamp`), plus how far this
        /// machine's time of day `here` is from the host's projected one (the follow converges, a few tenths at most).
        internal static double HostTotalNow(double total, double position, double stamp, double rate, double now, double here, double dayLength)
        {
            double ahead = Math.Max(0, now - stamp) * rate;
            double hostHere = ClockMath.Wrap(position + ahead, dayLength);
            return total + ahead + ClockMath.Difference(here, hostHere, dayLength);
        }

        /// A sleeping player's cloud curve (napszakvaltakozas.Felho's 0..1) `since` real seconds after their last report,
        /// running on at `rate` per second.
        internal static double SleepCurve(double progress, double since, double rate) =>
            Math.Min(1, Math.Max(0, progress) + Math.Max(0, since) * Math.Max(0, rate));

        /// How fast the sleeper's curve runs, from two reports `dt` real seconds apart. Nominally a sleep takes
        /// sleepRealTime real seconds (sleepRealTime * 2 game seconds at the game's time scale 2); measured on two hidden
        /// copies it took 9 of 8 (frame-time clamps), so the reports' own pace is taken, within a quarter and four times
        /// the nominal one. Reports arrive 0.25 to 2 s apart.
        internal static double SleepRate(double previous, double progress, double dt, double sleepRealS)
        {
            if (sleepRealS <= 0) return 1;
            double nominal = 1 / sleepRealS;
            if (previous < 0 || progress <= previous || dt < 0.1 || dt > 5) return nominal;
            return Math.Max(nominal * 0.25, Math.Min(nominal * 4, (progress - previous) / dt));
        }
    }
}
