using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    /// What the background watch (Entities.HealthWatch, Entities.ItemWatch) counts as a bug, and when it reports one. No game
    /// or Unity types: tools/unit/HealthWatchTest.cs runs it offline.
    ///
    /// Item health (Home) only runs when a player already noticed something; the 10:57 hood was found because the joiner
    /// tried every side of it. The same checks now run on their own on the shared objects near the player, and a problem
    /// that lasts (not a part being fitted, an item being picked up, a copy catching up) is an event in the session log
    /// and an automatic mark — the state of every machine at that moment, which a report written later never has.
    internal static class HealthWatchRules
    {
        /// The keys are fixed phrases: they go into bug reports, which keep only known words (TelemetryPrivacy.Results).
        internal const string Body = "health body", Position = "health position", NonFinite = "non-finite position",
                              Runaway = "runaway velocity", UnderGround = "under the ground", OwnerLeft = "owner left",
                              SlotMismatch = "part slot mismatch", RepairLoop = "repair keeps failing";
        /// Item watch: a loose shared item that shows but that the pickup ray can't hit.
        internal const string ItemOff = "item colliders off", ItemHeldLayer = "item on held layer", ItemNotPickup = "item not on pickup layer";

        internal static float AfterS = 6f;          // a problem this long (two or more passes) before it counts
        internal static float PositionAfterS = 10f; // a copy behind its owner: longer (a slow link catches up)

        /// How long a problem must last, by its key ("net:check"): a runaway or a position that is not a number is never a
        /// passing state, and the object is usually gone (streamed out, far) before a second look — at once.
        internal static float AfterOf(string key) =>
            key.EndsWith(Runaway, StringComparison.Ordinal) || key.EndsWith(NonFinite, StringComparison.Ordinal) ? 0f
            : key.EndsWith(Position, StringComparison.Ordinal) ? PositionAfterS : AfterS;
        internal static float OwnerLeftAfterS = 15f; // the host hands a leaver's objects on within a few seconds
        internal static float RepeatS = 300f;       // the same problem on the same object, reported again at most this often
        internal const float RunawayMps = 200f;     // 720 km/h: no car or item in the game gets near (physics blew up)
        internal const float UnderGroundM = 5f;     // this far under the ground's surface with nothing below it

        /// Why a loose item has no collider the pickup ray hits: `live` colliders switched on, `held` on the held-item layer.
        internal static string ItemWhy(int live, int held) => held > 0 ? ItemHeldLayer : live == 0 ? ItemOff : ItemNotPickup;

        internal static bool Finite(double x, double y, double z) => !double.IsNaN(x + y + z) && !double.IsInfinity(x + y + z);
        internal static bool IsRunaway(double vx, double vy, double vz) => !Finite(vx, vy, vz) || vx * vx + vy * vy + vz * vz > RunawayMps * RunawayMps;
        /// `groundY`: the ground's surface over the object (NaN: no ground over it). `floorBelow`: anything under it to stand on.
        internal static bool IsUnderGround(double y, double groundY, bool floorBelow) => !double.IsNaN(groundY) && y < groundY - UnderGroundM && !floorBelow;

        /// Problems seen on each pass, by key ("net:check"): one that lasts AfterS is due once; gone, it starts over; due
        /// again at most every RepeatS.
        internal sealed class Persist
        {
            private sealed class Seen { public double Since, Reported = double.NegativeInfinity; public bool Now; }
            private readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>(StringComparer.Ordinal);
            private readonly List<string> _drop = new List<string>();

            /// One pass over `scope` (keys starting with it: one object's): `current` the problems it has now. Returns the
            /// keys due now. `afterOf`: how long a key must last (default AfterS).
            internal List<string> Pass(string scope, ICollection<string> current, double now, Func<string, float> afterOf = null)
            {
                var due = new List<string>();
                foreach (var k in current)
                {
                    if (!_seen.TryGetValue(k, out var s)) { _seen[k] = s = new Seen { Since = now }; }
                    else if (!s.Now) s.Since = now;   // came back after clearing: a new episode
                    s.Now = true;
                    float after = afterOf != null ? afterOf(k) : AfterS;
                    if (now - s.Since >= after && now - s.Reported >= RepeatS) { s.Reported = now; due.Add(k); }
                }
                _drop.Clear();
                foreach (var kv in _seen)
                    if (kv.Key.StartsWith(scope, StringComparison.Ordinal) && !current.Contains(kv.Key))
                    {
                        if (now - kv.Value.Reported >= RepeatS) _drop.Add(kv.Key); else kv.Value.Now = false;
                    }
                foreach (var k in _drop) _seen.Remove(k);
                return due;
            }
            internal bool Active(string key) => _seen.TryGetValue(key, out var s) && s.Now && !double.IsNegativeInfinity(s.Reported);
            internal int Count => _seen.Count;
            internal void Clear() => _seen.Clear();
        }
    }
}
