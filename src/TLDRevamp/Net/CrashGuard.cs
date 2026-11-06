using System;

namespace TLDRevamp.Net
{
    /// Whether a crash with another player's car copy may become a crash takeover (Entities.TryTakeCrash): only when the
    /// copy is where its owner has the car. A takeover starts the car from the copy, so the owner's car jumps to wherever
    /// the copy is; a copy left behind (stuck on something, fed old poses) made a crash that happened only here move the
    /// real car. Bug report 2026-11-06 10:49: the host's copy of the joiner's car was 141 m behind it, the host's car hit
    /// that copy, the host took the car for 3 s and the joiner, seated, was put back 141 m with it.
    ///
    /// The copy is drawn one display delay behind the owner's present, so at speed it trails the owner's newest pose by
    /// speed x delay; the allowance is that plus a fixed margin. A car last seen moving with no pose newer than MaxPoseAgeS:
    /// where the owner has it is not known, and the crash stays with each machine's own physics (approach A). A car at rest
    /// sends nothing more (its last pose stays true however old), so its age doesn't count.
    internal static class CrashGuard
    {
        internal static float MaxOffM = 8f;          // beyond the display lag: a crash between two cars moves them a few metres
        internal static float LagSlackS = 0.25f;     // added to the display delay: jitter, one physics step, the pose interval
        internal static float MaxPoseAgeS = 1.0f;    // poses come 20 a second from a moving car
        internal static float RestMps = 0.5f;        // slower than this, the last pose is a car at rest
        /// Two cars head-on at full speed close at well under this. Faster is a physics artifact: a copy put back where it
        /// belongs after being left behind registered a contact at 4,383 m/s (crashguardcheck, 2026-11-06), and taken over
        /// at that speed the owner's car was flung 190 m/s.
        internal static float MaxRelMps = 150f;

        /// The reasons are fixed phrases: they go into bug reports, which keep only known words (TelemetryPrivacy.Results).
        internal const string NoPose = "no owner pose", OldPose = "owner pose old", Far = "copy far from owner", TooFast = "contact too fast";

        internal static bool SpeedFits(double relMps) => !double.IsNaN(relMps) && relMps <= MaxRelMps;

        /// offM: the copy's body to the owner's newest pose carried to the present. ageS: how old that pose is (owner time).
        /// allowM: how far the copy may be at this speed.
        internal static bool CopyFits(double offM, double ageS, double speedMps, double delayS, out string why, out double allowM)
        {
            why = null;
            allowM = MaxOffM + (double.IsNaN(speedMps) ? 0.0 : Math.Abs(speedMps)) * (Math.Max(0.0, delayS) + LagSlackS);
            if (double.IsNaN(offM) || double.IsInfinity(offM) || double.IsNaN(ageS)) { why = NoPose; return false; }
            if (ageS > MaxPoseAgeS && !(Math.Abs(speedMps) < RestMps)) { why = OldPose; return false; }
            if (offM > allowM) { why = Far; return false; }
            return true;
        }
    }
}
