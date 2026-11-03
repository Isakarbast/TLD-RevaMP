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
    /// roads it can be aligned to are then all there, on every machine. A building that can land near enough to be seen
    /// (the game's building distance + NeedMarginM; where it can land: Near) doesn't wait for the player to come closer:
    /// the road chunks it waits for are needed too (NeedForPois), only those and only while it waits. A whole extra ring of needed chunks cost
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
            // which road each waiting building aligns to: where it can end up depends on it (Near)
            Align.Clear();
            try
            {
                foreach (var key in __instance.chunksNeeded)
                    if (__instance.chunks.TryGetValue(key, out var ch) && ch.poiToPlace != null)
                        foreach (var t in ch.poiToPlace) if (t != null) Align[t.pos] = t.roadType;
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiRoadsSettled align: " + e.Message); }
        }

        private static readonly Dictionary<Vector3d, poiGenScript.alignToRoadEmu> Align = new Dictionary<Vector3d, poiGenScript.alignToRoadEmu>();

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
                        if (!nearKnown) { near = Near(g, _gpos); nearKnown = true; }
                        if (!near) break;
                        if (!g.chunksNeeded.Contains(k)) Want[k] = Time.realtimeSinceStartup;
                    }
                if (!held) return;
                __result = false;
                Held++;
                NoteHeld(g, _gpos);
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiRoadsSettled: " + e.Message); }
        }

        private static bool Finished(roadGenScript g, Vector3d pos)
        {
            if (!g.chunksNeeded.Contains(pos) || !g.chunks.TryGetValue(pos, out var chunk)) return false;
            return chunk.insideConnected && chunk.outsideUConnected && chunk.outsideRConnected && chunk.Done();
        }

        /// Can the building land within the game's building distance (+ margin) of this player: would it be seen? A
        /// building aligned to a road lands by the road nearest its candidate spot, so within that road's distance of it,
        /// and the nearest road can only come nearer as more roads are made: the nearest road of its kind known now bounds
        /// it (each road's bones stay within AlignNearestRoad.MaxLateral of the line between its ends). A main road
        /// building's candidate can be 5 km and more off the main road (2026-11-03 tour: candidate 10 km from the player,
        /// building 5 km). Bounds are kept for BoundForS.
        private static bool Near(roadGenScript g, Vector3d cand)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return false;
            var me = mainscript.GlobalFromUnityPos(pl.transform.position);
            double dx = cand.x - me.x, dz = cand.z - me.z;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d < _range) return true;
            if (!Align.TryGetValue(cand, out var type) || type == poiGenScript.alignToRoadEmu.none) return false;   // not aligned: it stays at its spot
            return d - Bound(g, cand, type) < _range;
        }

        public static float BoundForS = 2f, UnknownRoadM = 12000f, PlaceSlackM = 200f;
        private struct CachedBound { public double M; public float At; }
        private static readonly Dictionary<Vector3d, CachedBound> Bounds = new Dictionary<Vector3d, CachedBound>();

        /// How far from its candidate a building aligned to a road of this kind can land, at most.
        private static double Bound(roadGenScript g, Vector3d cand, poiGenScript.alignToRoadEmu type)
        {
            float now = Time.realtimeSinceStartup;
            if (Bounds.TryGetValue(cand, out var c) && now - c.At < BoundForS) return c.M;
            if (Bounds.Count > 4096) Bounds.Clear();
            double best = UnknownRoadM, lateral = AlignNearestRoad.MaxLateral(g);
            foreach (var ch in g.chunks.Values)
                foreach (var r in ch.roads)
                {
                    if (r == null || (g.hasMainRoad && !g.CheckAlignRoad(r.roadType, type))) continue;
                    double m = AlignNearestRoad.SegmentDist(cand, r.start, r.end) + lateral;
                    if (m < best) best = m;
                }
            best += PlaceSlackM;
            Bounds[cand] = new CachedBound { M = best, At = now };
            return best;
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

        /// The nearest a building held back in the last second could land from the player, against how far the game
        /// shows buildings: one that could land inside that distance would appear late in sight (bridge
        /// `get TLDRevamp.Fixes.PoiRoadsSettled.Stats`).
        private static double _nearest = double.MaxValue, _nearestShown = double.MaxValue;
        private static float _nearestAt;
        private static void NoteHeld(roadGenScript g, Vector3d cand)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;
            if (Time.realtimeSinceStartup - _nearestAt > 1f) { _nearestShown = _nearest; _nearest = double.MaxValue; _nearestAt = Time.realtimeSinceStartup; }
            var me = mainscript.GlobalFromUnityPos(pl.transform.position);
            double dx = cand.x - me.x, dz = cand.z - me.z, d = Math.Sqrt(dx * dx + dz * dz);
            if (Align.TryGetValue(cand, out var type) && type != poiGenScript.alignToRoadEmu.none) d = Math.Max(0, d - Bound(g, cand, type));
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
