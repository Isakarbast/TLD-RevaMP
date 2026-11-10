using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The shared objects placed in the world here (their item loaded), once a frame. A world's shared objects pile up as
    /// players go places, and nearly all of them are stored away far off: the DevTest world of 2026-11-10 had ~12,000
    /// (9,894 items, 1,134 animals, 162 cars), ~25 of them placed. The per-frame loops (hitches, item pushes, copies moved
    /// by body, cargo zones, the collider LOD, resync, the owner's states) each walked all of them for those few, a Unity
    /// null check each: 11 ms a frame on the host (tools/perfmp.py), and in the session 2 physics steps a frame — 35 ms
    /// frames where the same world in 11-06's session (~1,200) took 16. Those loops walk this list now.
    ///
    /// The full pass over every object binds what the game placed since (Resolve: what ResyncFrameTick did for every object
    /// every frame) and drops the destroyed item a stored-away object pointed at (a plain null check after that). It runs
    /// when something may have changed: the game's placed items or the shared objects counted differently, an object bound
    /// or replaced elsewhere (_placedDirty), or every FullEveryFrames frames. Otherwise last frame's list is kept, less what
    /// went: 1.4 ms a frame at the DevTest gas station before, standing. A/B: LoopPlacedOnly false = every loop walks every
    /// object, as before.
    public static partial class Entities
    {
        public static bool LoopPlacedOnly = true;
        public static int FullEveryFrames = 30, DormantEveryFrames = 15;
        public static long PlacedRebuilds, PlacedFullPasses;
        public static int PlacedCount;

        private static readonly List<Ent> _placed = new List<Ent>(), _all = new List<Ent>();
        private static int _placedFrame = -1, _fullFrame = -1000, _itemsSeen = -1, _netsSeen = -1, _dormantFrame = -1000;
        private static bool _placedDirty = true;
        /// Host: the server's objects locked in something (a container, a car's bed), which follow it at its states.
        private static readonly HashSet<SEnt> _serverLocked = new HashSet<SEnt>();

        /// This frame's placed objects (every shared object with LoopPlacedOnly off). Valid until the next frame; an object
        /// removed meanwhile can still be in it (its Root then gone: the loops skip it as before).
        private static List<Ent> Placed()
        {
            if (_placedFrame == Time.frameCount) return _placed;
            _placedFrame = Time.frameCount; PlacedRebuilds++;
            if (!LoopPlacedOnly) { _placed.Clear(); _placed.AddRange(ByNet.Values); PlacedCount = _placed.Count; return _placed; }
            int items = savedatascript.s != null && savedatascript.s.items != null ? savedatascript.s.items.Count : -1;
            if (_placedDirty || items != _itemsSeen || ByNet.Count != _netsSeen || Time.frameCount - _fullFrame >= FullEveryFrames)
            {
                _placedDirty = false; _itemsSeen = items; _netsSeen = ByNet.Count; _fullFrame = Time.frameCount; PlacedFullPasses++;
                _placed.Clear();
                foreach (var e in ByNet.Values)
                {
                    if (!ReferenceEquals(e.Root, null) && e.Root == null) StoredAway(e);
                    if (ReferenceEquals(e.Root, null) && (e.RootId == 0 || !Resolve(e))) continue;
                    _placed.Add(e);
                }
            }
            else
            {
                // nothing placed or shared since: last frame's, less what was stored away or removed
                for (int i = _placed.Count - 1; i >= 0; i--)
                {
                    var e = _placed[i];
                    if (!ReferenceEquals(e.Root, null) && e.Root == null) StoredAway(e);
                    if (ReferenceEquals(e.Root, null) || !ByNet.TryGetValue(e.NetId, out var now) || now != e) _placed.RemoveAt(i);
                }
            }
            PlacedCount = _placed.Count;
            return _placed;
        }

        /// The game destroyed its item: a plain null from now on, and what a copy moved by its body had to stop
        /// (BodyMotionTick did it for every unplaced object each step).
        private static void StoredAway(Ent e)
        {
            e.Root = null;
            if (e.MovedByBody) e.MovedByBody = false;
        }

        /// Every shared object (the loops with LoopPlacedOnly off).
        private static List<Ent> AllEnts() { _all.Clear(); _all.AddRange(ByNet.Values); return _all; }

        /// A stored-away object's far record: a copy riding a container follows it there, and a pose waiting goes out.
        private static void DormantTick(Ent e)
        {
            if (e.Proxy && e.PoseLocked && e.ReceivedStateEpoch == e.Epoch && LockedWorld(e, out var lockedPos, out var lockedRot)) UpdateFarRecord(e, lockedPos, lockedRot);
            if (e.FarDirty && Time.realtimeSinceStartup >= e.FarFlushAt) { e.FarFlushAt = Time.realtimeSinceStartup + 1f; FlushFarRecords(e); }
        }
    }
}
