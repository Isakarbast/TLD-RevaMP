using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The shared objects placed in the world here (their item loaded), once a frame. A world's shared objects pile up as
    /// players go places, and nearly all of them are stored away far off: the DevTest world of 2026-11-10 had ~12,000
    /// (9,894 items, 1,134 animals, 162 cars), ~25 of them placed. The per-frame loops (hitches, item pushes, copies moved
    /// by body, cargo zones, the collider LOD, resync) each walked all of them for those few, a Unity null check each:
    /// 11 ms a frame on the host (tools/perfmp.py), and in the session 2 physics steps a frame — 35 ms frames where the
    /// same world in 11-06's session (~1,200) took 16. Those loops walk this list now. Building it binds what the game
    /// placed since (Resolve: what ResyncFrameTick did for every object every frame), and an object stored away loses
    /// the destroyed item it pointed at, so the next frame checks a plain null. A/B: false = the loops walk every object.
    public static partial class Entities
    {
        public static bool LoopPlacedOnly = true;
        public static long PlacedRebuilds;
        public static int PlacedCount;

        private static readonly List<Ent> _placed = new List<Ent>();
        private static int _placedFrame = -1;

        /// This frame's placed objects (every shared object with LoopPlacedOnly off). Valid until the next frame; an object
        /// removed meanwhile can still be in it (its Root then gone: the loops skip it as before).
        private static List<Ent> Placed()
        {
            if (_placedFrame == Time.frameCount) return _placed;
            _placedFrame = Time.frameCount; PlacedRebuilds++;
            _placed.Clear();
            if (!LoopPlacedOnly) { _placed.AddRange(ByNet.Values); PlacedCount = _placed.Count; return _placed; }
            foreach (var e in ByNet.Values)
            {
                if (!ReferenceEquals(e.Root, null) && e.Root == null)
                {
                    // stored away (the game destroyed its item): a plain null from now on, and what a copy moved by its
                    // body had to stop (BodyMotionTick did it for every unplaced object each step)
                    e.Root = null;
                    if (e.MovedByBody) e.MovedByBody = false;
                }
                if (ReferenceEquals(e.Root, null) && (e.RootId == 0 || !Resolve(e))) continue;
                _placed.Add(e);
            }
            PlacedCount = _placed.Count;
            return _placed;
        }
    }
}
