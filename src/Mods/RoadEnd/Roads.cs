using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RoadEnd
{
    /// The main road (the one with the power poles) stops at its end (EndPlan: a distance along the road); side
    /// roads, the land and everything on it go on. A main road's pieces (roadElementClass, ~100 m of 10 m bones each)
    /// are cut right after roadClass.Generate made them, before anything places them or shapes the terrain to them:
    /// roads that come after the end on the drive go, the one it is on keeps its bones up to it. The road's entries
    /// in the chunk's inner-chunk index (what UpdPlaceRemove places and RoadsNear finds) follow.
    [HarmonyPatch]
    public static class Roads
    {
        public static int Trimmed, Dropped;

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunks))]
        [HarmonyPostfix]
        private static void AfterChunks(roadGenScript __instance)
        {
            try { EndPlan.Update(__instance); }
            catch (Exception e) { Plugin.Once("end plan", e); }
        }

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.Restart))]
        [HarmonyPostfix]
        private static void AfterRestart()
        {
            EndPlan.Reset();
            Grave.Reset();
        }

        [HarmonyPatch(typeof(roadGenScript.roadClass), nameof(roadGenScript.roadClass.Generate))]
        [HarmonyPostfix]
        private static void AfterGenerate(roadGenScript.roadClass __instance, roadGenScript _roadgen)
        {
            if (!World.Active) return;
            try { Trim(__instance, _roadgen); }
            catch (Exception e) { Plugin.Once("road trim", e); }
        }

        /// Roads cut away entirely have no pieces: NearestPointOnRoadBone would answer the asked point itself for them
        /// (distance 0), so nothing may find them.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsNear))]
        [HarmonyPostfix]
        private static void AfterRoadsNear(List<roadGenScript.roadClass> __result)
        {
            if (!World.Active || __result == null) return;
            for (int i = __result.Count - 1; i >= 0; i--)
                if (__result[i] != null && __result[i].generated && __result[i].roadElements.Count == 0) __result.RemoveAt(i);
        }

        internal static void Trim(roadGenScript.roadClass road, roadGenScript rg)
        {
            var els = road.roadElements;
            if (els.Count == 0 || !EndPlan.Current.Known || !EndPlan.Key(road, rg, out int chunk, out int step)) return;
            int where = EndPlan.Compare(chunk, step);
            if (where < 0) return;
            int before = els.Count;
            var keep = new List<roadGenScript.roadElementClass>();
            var oldIndex = new List<int>();
            // the road the end is on: its bones run north; the drive's first Along metres of them stay
            bool north = World.Sign > 0;
            if (where == 0) KeepAlong(els, EndPlan.Current.Along, north, keep, oldIndex);
            foreach (var e in els) if (!keep.Contains(e) && e.obj != null) UnityEngine.Object.Destroy(e.obj);
            els.Clear();
            els.AddRange(keep);
            Reindex(road, rg, oldIndex);
            if (keep.Count == 0) Dropped++; else Trimmed++;
            if (where == 0)
            {
                Vector3d at, from;
                if (keep.Count > 0)
                {
                    var first = keep[0].bonePos; var last = keep[keep.Count - 1].bonePos;
                    if (north) { at = last[last.Count - 1]; from = last.Count >= 2 ? last[last.Count - 2] : first[0]; }
                    else { at = first[0]; from = first.Count >= 2 ? first[1] : last[last.Count - 1]; }
                }
                else { at = road.start; from = at - new Vector3d(0, 0, World.Sign); }
                EndPlan.SetRoadEnd(at, at - from);
            }
            if (Plugin.Verbose) Plugin.Log.LogInfo($"road {road.selfChunk.z:0}/{road.selfID} chunk {chunk} step {step}: {before} pieces -> {keep.Count}");
        }

        /// The road's pieces up to `along` metres of bones from its start on the drive (its first bone when the drive
        /// runs north, its last when south); the piece the end falls in keeps its bones up to there.
        private static void KeepAlong(List<roadGenScript.roadElementClass> els, double along, bool north, List<roadGenScript.roadElementClass> keep, List<int> oldIndex)
        {
            double done = 0;
            void Keep(int i) { if (north) { keep.Add(els[i]); oldIndex.Add(i); } else { keep.Insert(0, els[i]); oldIndex.Insert(0, i); } }
            for (int k = 0; k < els.Count; k++)
            {
                int i = north ? k : els.Count - 1 - k;
                var bones = els[i].bonePos;
                if (bones.Count == 0) continue;
                var order = new List<Vector3d>(bones);
                if (!north) order.Reverse();
                int n = 1; Vector3d? cross = null;
                for (; n < order.Count; n++)
                {
                    double l = EndPlan.Dist(order[n - 1], order[n]);
                    if (done + l >= along) { cross = order[n - 1] + (order[n] - order[n - 1]) * ((along - done) / Math.Max(l, 1e-6)); break; }
                    done += l;
                }
                if (cross == null) { Keep(i); continue; }
                var kept = order.GetRange(0, n);
                var flat = cross.Value - kept[kept.Count - 1]; flat.y = 0;
                if (flat.magnitude >= 1.0) kept.Add(cross.Value);
                if (kept.Count >= 2)
                {
                    if (!north) kept.Reverse();
                    bones.Clear(); bones.AddRange(kept);
                    Keep(i);
                }
                return;
            }
        }

        /// The last power pole: the game strings each pole's wires to the next piece's pole, and a pole with none keeps
        /// its wires at their prefab length, 100 m straight on into nothing. The end road's last piece (on a drive
        /// north; going south the wires point back the way the road came) has none: its wires go. Found by its place on
        /// the drive, not the road object: the game makes a road's pieces again when it comes back to a chunk.
        [HarmonyPatch(typeof(roadscript), nameof(roadscript.PowerPole))]
        [HarmonyPostfix]
        private static void AfterPowerPole(roadscript __instance)
        {
            if (!World.Active || World.Sign < 0 || !EndPlan.Current.Known) return;
            try
            {
                var road = RoadOf(__instance);
                if (road == null || __instance.element != road.roadElements.Count - 1) return;
                if (!EndPlan.Key(road, __instance.roadgen, out int c, out int st) || EndPlan.Compare(c, st) != 0) return;
                var rope = __instance.GetRope();
                if (rope == null || rope.ropes == null) return;
                foreach (var w in rope.ropes) if (w != null) w.localScale = Vector3.zero;
            }
            catch (Exception e) { Plugin.Once("last pole", e); }
        }

        /// A road's last pole looks for the next road's in the chunk's main road list, in the list's order; with roads
        /// cut away that order can lead to a road with no pieces, and the wires hang. When the game finds none, the
        /// next road on the drive north is the one.
        [HarmonyPatch(typeof(roadscript), nameof(roadscript.GetNextRoad))]
        [HarmonyPostfix]
        private static void AfterNextRoad(roadscript __instance, ref roadscript __result)
        {
            if (__result != null || !World.Active || World.Sign < 0 || !EndPlan.Current.Known) return;
            try
            {
                var rg = __instance.roadgen;
                var road = RoadOf(__instance);
                if (road == null || __instance.element != road.roadElements.Count - 1) return;
                if (!EndPlan.Key(road, rg, out int c, out int st) || EndPlan.Compare(c, st) >= 0) return;
                // after a link road the next chunk's first piece; after a piece the next one, or the link road on
                var next = st == EndPlan.Link ? Find(rg, c + 1, 0) : Find(rg, c, st + 1) ?? Find(rg, c, EndPlan.Link);
                if (next == null || next.roadElements.Count == 0) return;
                var el = next.roadElements[next.roadElements.Count > 1 ? 1 : 0];
                var rs = el.obj != null ? el.obj.GetComponent<roadscript>() : null;
                if (rs != null && rs.GetRope() != null) __result = rs;
            }
            catch (Exception e) { Plugin.Once("next pole", e); }
        }

        internal static roadGenScript.roadClass RoadOf(roadscript r)
        {
            var rg = r != null ? r.roadgen : null;
            return rg != null && rg.chunks.TryGetValue(r.chunk, out var ch) && r.roadID >= 0 && r.roadID < ch.roads.Count ? ch.roads[r.roadID] : null;
        }

        /// The main road at a place on the drive north (its chunk and step; a link road is kept by either chunk).
        private static roadGenScript.roadClass Find(roadGenScript rg, int chunk, int step)
        {
            double cs = rg.chunkSize;
            for (int k = chunk; k <= chunk + (step == EndPlan.Link ? 1 : 0); k++)
            {
                if (!rg.chunks.TryGetValue(new Vector3d(0, 0, k * cs), out var ch)) continue;
                foreach (var r in ch.roads)
                    if (EndPlan.Key(r, rg, out int c, out int st) && c == chunk && st == step) return r;
            }
            return null;
        }

        /// The chunk's inner-chunk index lists piece numbers per road: renumber them after the cut, drop gone pieces.
        private static void Reindex(roadGenScript.roadClass road, roadGenScript rg, List<int> oldIndex)
        {
            if (rg == null || !rg.chunks.TryGetValue(road.selfChunk, out var chunk)) return;
            var map = new Dictionary<int, int>();
            for (int i = 0; i < oldIndex.Count; i++) map[oldIndex[i]] = i;
            foreach (var inner in chunk.innerChunks.Values)
            {
                for (int r = inner.roads.Count - 1; r >= 0; r--)
                {
                    var rc = inner.roads[r];
                    if (rc.roadId != road.selfID) continue;
                    var renumbered = new List<int>();
                    foreach (int e in rc.roadElements) if (map.TryGetValue(e, out int n) && !renumbered.Contains(n)) renumbered.Add(n);
                    rc.roadElements.Clear();
                    rc.roadElements.AddRange(renumbered);
                    if (renumbered.Count == 0) inner.roads.RemoveAt(r);
                }
            }
        }
    }
}
