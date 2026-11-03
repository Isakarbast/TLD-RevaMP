using System;
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
    /// roads it can be aligned to are then all there, on every machine. So that buildings at the edge of the area don't
    /// wait until the player comes a chunk closer, the road generator keeps one more ring of chunks around the area it
    /// needs (NeedAround). Main road only worlds have their own rule (WorldGen.MainRoadOnlyRoads.PoisWaitForLinks).
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.PoiRoadsSettled.Enabled / .NeedAround.
    [HarmonyPatch]
    public static class PoiRoadsSettled
    {
        public static bool Enabled = true, NeedAround = true;
        public static long Held, ChunksAdded;
        [ThreadStatic] private static int _inAddPois;

        private static bool Applies(roadGenScript g) => g != null && g.hasSideRoad;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyPrefix]
        private static void BeforeAddPois() => _inAddPois++;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyFinalizer]
        private static void AfterAddPois() => _inAddPois--;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsDoneAtPlace))]
        [HarmonyPostfix]
        private static void Settled(roadGenScript __instance, Vector3d _gpos, ref bool __result)
        {
            if (!__result || _inAddPois == 0 || !Enabled || !Applies(__instance)) return;
            try
            {
                var g = __instance;
                var c = mainscript.GetChunkPos(_gpos, 0, 0, g.chunkSize);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (!Finished(g, mainscript.GetChunkPos(c, dx, dz, g.chunkSize)))
                        {
                            __result = false;
                            Held++;
                            NoteHeld(_gpos);
                            return;
                        }
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiRoadsSettled: " + e.Message); }
        }

        private static bool Finished(roadGenScript g, Vector3d pos)
        {
            if (!g.chunksNeeded.Contains(pos) || !g.chunks.TryGetValue(pos, out var chunk)) return false;
            return chunk.insideConnected && chunk.outsideUConnected && chunk.outsideRConnected && chunk.Done();
        }

        /// The needed chunks and every neighbour of them: a building in the needed area has all nine chunks around it
        /// needed. The per-frame overload only, not UpdChunksNeeded(Vector3d).
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunksNeeded), new Type[0])]
        [HarmonyPostfix]
        private static void NeedRingAround(roadGenScript __instance)
        {
            var g = __instance;
            if (!Enabled || !NeedAround || !Applies(g) || g.chunksNeeded == null) return;
            try
            {
                int n = g.chunksNeeded.Count;
                for (int i = 0; i < n; i++)
                {
                    var c = g.chunksNeeded[i];
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            var k = mainscript.GetChunkPos(c, dx, dz, g.chunkSize);
                            if (!g.chunksNeeded.Contains(k)) { g.chunksNeeded.Add(k); ChunksAdded++; }
                        }
                }
                if (g.chunksNeeded.Count != n) g.UpdChunksToRemove();
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiRoadsSettled ring: " + e.Message); }
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
                return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"needAround\":" + (NeedAround ? "true" : "false") +
                    ",\"held\":" + Held + ",\"chunksAdded\":" + ChunksAdded +
                    ",\"nearestHeldM\":" + (near < double.MaxValue ? near.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "-1") +
                    ",\"buildingsShownWithinM\":" + (pg != null ? pg.lodDistance.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "-1") +
                    ",\"buildingsRemovedBeyondM\":" + (pg != null ? pg.removeDistance.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "-1") + "}";
            }
        }
    }
}
