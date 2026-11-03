using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Game bug (every terrain, poiGenScript.AddPoi): two buildings whose candidate spots both align to the same road
    /// point (rounded to poiGenScript.round) can't both stand there; the game keeps the one placed first and drops the
    /// other for good. Which comes first follows the order the areas were reached in, so two players get different
    /// buildings at one spot. Measured (main road only, worldtour.py, two copies driving in from opposite sides,
    /// 2026-11-03): at (349, -84832) one copy had building 11, the other building 4, neither candidate left waiting.
    ///
    /// Fix: the candidate whose spot comes first (smallest x, then z) wins, whatever the order: a later candidate that
    /// wins takes the place of the one standing there, as long as that one isn't spawned yet (no GameObject, nothing
    /// from the save). A spawned loser stays (counted: KeptSpawned); buildings near the player get their roads at once
    /// (PoiRoadsSettled), so both are decided before either is spawned. Buildings that came from the save, the start
    /// house and buildings placed by other code have no candidate here and are never replaced.
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.PoiSpotOrder.Enabled.
    [HarmonyPatch]
    public static class PoiSpotOrder
    {
        public static bool Enabled = true;
        public static long Collisions, Replaced, KeptSpawned, KeptFirst;

        /// The candidate spot each building came from.
        private sealed class Cand { public Vector3d Pos; }
        private static readonly ConditionalWeakTable<poiGenScript.poiClass, Cand> CandOf = new ConditionalWeakTable<poiGenScript.poiClass, Cand>();

        [ThreadStatic] private static int _inAddPois;
        private static bool _haveCand;
        private static Vector3d _cand;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyPrefix]
        private static void BeforeAddPois() { _inAddPois++; _haveCand = false; }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyFinalizer]
        private static void AfterAddPois() { _inAddPois--; _haveCand = false; }

        /// The candidate AddPois is about to place: the last spot whose roads were found done.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsDoneAtPlace))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]   // after every rule that holds a building back
        private static void Candidate(Vector3d _gpos, bool __result)
        {
            if (_inAddPois == 0 || !__result) return;
            _cand = _gpos; _haveCand = true;
        }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPoi), new[] { typeof(Vector3d), typeof(Vector3), typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        private static void Before(poiGenScript __instance, Vector3d _pos, bool _alignedToRoad, out Vector3d? __state)
        {
            __state = null;
            if (_inAddPois == 0) return;
            // an unaligned building stands at its own candidate spot (no roads asked: a far building, or no road generator)
            Vector3d cand = _haveCand ? _cand : _pos;
            if (!_haveCand && _alignedToRoad) return;
            __state = cand;
            _haveCand = false;
            if (!Enabled) return;
            try
            {
                var chunkPos = mainscript.GetChunkPos(_pos, __instance.chunkSize);
                var key = mainscript.Snap(_pos, __instance.round);
                if (!__instance.chunks.TryGetValue(chunkPos, out var ch) || !ch.pois.TryGetValue(key, out var old)) return;
                Collisions++;
                if (!CandOf.TryGetValue(old, out var oc) || !First(cand, oc.Pos)) { KeptFirst++; return; }
                if (old.pobj != null || old.spawnedItems || old.spawnedFixed) { KeptSpawned++; return; }
                ch.pois.Remove(key);   // the original adds this one in its place
                Replaced++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoiSpotOrder: " + e.Message); }
        }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPoi), new[] { typeof(Vector3d), typeof(Vector3), typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        private static void After(poiGenScript.poiClass __result, Vector3d? __state)
        {
            if (__result == null || __state == null || CandOf.TryGetValue(__result, out _)) return;
            CandOf.Add(__result, new Cand { Pos = __state.Value });
        }

        /// A building AddPois placed from a candidate (not from the save, not the start house or a fixed one).
        internal static bool FromCandidate(poiGenScript.poiClass p) => p != null && CandOf.TryGetValue(p, out _);

        private static bool First(Vector3d a, Vector3d b) => a.x < b.x || (a.x == b.x && a.z < b.z);

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"collisions\":" + Collisions + ",\"replaced\":" + Replaced +
            ",\"keptFirst\":" + KeptFirst + ",\"keptSpawned\":" + KeptSpawned + "}";
    }
}
