using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Game bug (crossroads worlds, poiGenScript.AddPois): where a building stands depends on which road chunks around it
    /// happened to be finished when it was placed, so two players (or one before and after a reload) get buildings in
    /// other places, or a building only one of them has.
    ///
    /// A building is placed once roadGenScript.RoadsDoneAtPlace is true, and is aligned to the nearest road
    /// AlignPosToRoad finds then (RoadsNear: the roads of the 3x3 road chunks around it). RoadsDoneAtPlace looks at four of
    /// those nine chunks and asks Done(): every road of the chunk generated, which is also true for a chunk with no roads
    /// at all. The game remakes the chunks at the edge of the needed area every frame with no roads yet, so a building
    /// next to one passes for a frame and is aligned to whatever roads exist at that moment. Measured (two copies at one
    /// road-chunk corner, one arriving from 30 km north, one from the south, 2026-11-03, worldcheck.py): roads identical,
    /// but 4 buildings on one machine only and 1 on the other.
    ///
    /// Fix: a building waits until all nine road chunks around it are needed (kept, not remade every frame), have their
    /// dots joined into roads, have their links to the chunks above and right made, and have every road generated: the
    /// roads it can be aligned to are then all there, on every machine. A building near enough to be seen
    /// (the game's building distance + NeedMarginM) doesn't wait for the player to come closer: the road chunks it waits
    /// for are needed too (NeedForPois), only those and only while it waits. A whole extra ring of needed chunks cost
    /// +0.6 ms per frame while travelling (road piece placement walks every needed chunk); farther buildings wait unseen.
    /// Main road only worlds have their own rule (WorldGen.MainRoadOnlyRoads.PoisWaitForLinks).
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.PoiRoadsSettled.Enabled / .NeedForPois / .NeedMarginM.
    [HarmonyPatch]
    public static class PoiRoadsSettled
    {
        public static bool Enabled = true, NeedForPois = true;
        public static float NeedMarginM = 1500f, WantForS = 5f;
        public static long Held, ChunksAdded;
        [ThreadStatic] private static int _inAddPois;

        /// Road chunks a near building waits for, with when it last asked.
        private static readonly Dictionary<Vector3d, float> Want = new Dictionary<Vector3d, float>();

        private static bool Applies(roadGenScript g) => g != null && g.hasSideRoad;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyPrefix]
        private static void BeforeAddPois(poiGenScript __instance)
        {
            _inAddPois++;
            _range = __instance.removeDistance + NeedMarginM;
        }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyFinalizer]
        private static void AfterAddPois() => _inAddPois--;

        private static float _range;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsDoneAtPlace))]
        [HarmonyPostfix]
        private static void Settled(roadGenScript __instance, Vector3d _gpos, ref bool __result)
        {
            if (!__result || _inAddPois == 0 || !Enabled || !Applies(__instance)) return;
            try
            {
                var g = __instance;
                var c = mainscript.GetChunkPos(_gpos, 0, 0, g.chunkSize);
                bool held = false, near = false, nearKnown = false;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var k = mainscript.GetChunkPos(c, dx, dz, g.chunkSize);
                        if (Finished(g, k)) continue;
                        held = true;
                        if (!NeedForPois) break;
                        if (!nearKnown) { near = Near(_gpos); nearKnown = true; }
                        if (!near) break;
                        if (!g.chunksNeeded.Contains(k)) Want[k] = Time.realtimeSinceStartup;
                    }
                if (!held) return;
                __result = false;
                Held++;
                NoteHeld(_gpos);
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiRoadsSettled: " + e.Message); }
        }

        private static bool Finished(roadGenScript g, Vector3d pos)
        {
            if (!g.chunksNeeded.Contains(pos) || !g.chunks.TryGetValue(pos, out var chunk)) return false;
            return chunk.insideConnected && chunk.outsideUConnected && chunk.outsideRConnected && chunk.Done();
        }

        /// Within the game's building distance (+ margin) of this player: a building that would be seen.
        private static bool Near(Vector3d gpos)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return false;
            var me = mainscript.GlobalFromUnityPos(pl.transform.position);
            double dx = gpos.x - me.x, dz = gpos.z - me.z;
            return dx * dx + dz * dz < (double)_range * _range;
        }

        /// The chunks near buildings wait for, needed too while they wait (the per-frame overload, not
        /// UpdChunksNeeded(Vector3d)). Each is a neighbour of a needed chunk: a building's chunk is needed by its generator.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunksNeeded), new Type[0])]
        [HarmonyPostfix]
        private static void NeedWanted(roadGenScript __instance)
        {
            var g = __instance;
            if (!Enabled || !NeedForPois || !Applies(g) || g.chunksNeeded == null || Want.Count == 0) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                List<Vector3d> old = null;
                int n = g.chunksNeeded.Count;
                foreach (var kv in Want)
                {
                    if (now - kv.Value > WantForS) { (old ?? (old = new List<Vector3d>())).Add(kv.Key); continue; }
                    if (!g.chunksNeeded.Contains(kv.Key)) { g.chunksNeeded.Add(kv.Key); ChunksAdded++; }
                }
                if (old != null) foreach (var k in old) Want.Remove(k);
                if (g.chunksNeeded.Count != n) g.UpdChunksToRemove();
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiRoadsSettled need: " + e.Message); }
        }

        /// The nearest building held back from the player in the last second, against how far the game shows buildings:
        /// one held inside that distance would appear late in sight (bridge `get TLDRevamp.Fixes.PoiRoadsSettled.Stats`).
        private static double _nearest = double.MaxValue, _nearestShown = double.MaxValue;
        private static float _nearestAt;
        private static void NoteHeld(Vector3d gpos)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;
            if (Time.realtimeSinceStartup - _nearestAt > 1f) { _nearestShown = _nearest; _nearest = double.MaxValue; _nearestAt = Time.realtimeSinceStartup; }
            var me = mainscript.GlobalFromUnityPos(pl.transform.position);
            double dx = gpos.x - me.x, dz = gpos.z - me.z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d < _nearest) _nearest = d;
        }

        public static string Stats
        {
            get
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                var pg = map != null && map.poiGens != null && map.poiGens.Count > 0 ? map.poiGens[0] : null;
                double near = Math.Min(_nearest, _nearestShown);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"needForPois\":" + (NeedForPois ? "true" : "false") +
                    ",\"held\":" + Held + ",\"chunksAdded\":" + ChunksAdded + ",\"wanted\":" + Want.Count +
                    ",\"nearestHeldM\":" + (near < double.MaxValue ? near.ToString("0", ci) : "-1") +
                    ",\"buildingsShownWithinM\":" + (pg != null ? pg.lodDistance.ToString("0", ci) : "-1") +
                    ",\"buildingsRemovedBeyondM\":" + (pg != null ? pg.removeDistance.ToString("0", ci) : "-1") + "}";
            }
        }
    }
}
