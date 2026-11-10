using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's item streaming (itemPlaceRemoveScript, once a second) places stored items within itemSpawnDist and
    /// unfreezes every item within itemUnFreezeDist (tosaveitemscript.DistUnFreezeRB: isKinematic = false) — with no
    /// check that anything is under it yet. Arriving faster than the world generates (a teleport, a join at another
    /// player, a slow machine), items are dropped before the building's floor or the terrain collider exists: they fall
    /// through and are gone (farworld run 4: 242 m; run 10: a building's items 245 m down, on the host arriving).
    ///
    /// An item about to be unfrozen first looks straight down (1 km, solid colliders only, not its own): nothing there →
    /// it stays frozen this second and is asked again the next. Already unfrozen items are not checked again.
    ///
    /// Anything within 1 km was enough before: inside a building whose floor isn't there yet (the game places a building
    /// once the roads around it are done, its items stream in by distance) the ray found the terrain far under the floor,
    /// and the items fell (Volkan, 2026-11-10: "items in building fall under the ground since the buildings ground only
    /// loads later"; his autosave held a car, a bus front and ~30 items 320-410 m under the gas station). Now what it
    /// stands on must be within reach (NearM): the item's lowest collider to the nearest hit below. Out of reach it is held
    /// too, at most HoldMaxS, then let go as before. A/B: Near false = anything within RayM counts (before).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.UnfreezeOnGround.Enabled
    [HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.DistUnFreezeRB))]
    public static class UnfreezeOnGround
    {
        public static bool Enabled = true, Near = true;
        public static float RayM = 1000f, NearM = 1.5f, HoldMaxS = 120f;
        public static long Held, Released, HeldFar, TimedOut;
        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private static readonly System.Collections.Generic.Dictionary<int, float> _held = new System.Collections.Generic.Dictionary<int, float>();
        private static readonly System.Collections.Generic.List<Collider> _cols = new System.Collections.Generic.List<Collider>();

        [HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool Prefix(tosaveitemscript __instance)
        {
            if (Net.Tpa.Holding(__instance.RB)) return false;
            if (!Enabled || __instance.RB == null || !__instance.RB.isKinematic || __instance.buried) return true;
            var root = __instance.transform.root;
            var p = __instance.transform.position;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.5f, Vector3.down, _hits, RayM, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;   // the hits come in no order
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || c.transform.root == root) continue;
                if (_hits[i].distance < nearest) nearest = _hits[i].distance;
            }
            int id = __instance.GetInstanceID();
            bool something = nearest < float.MaxValue;
            // the reach rule for loose items only: one in something (a car's trunk, bolted on) has that something as its
            // root, and its whole hierarchy's colliders (a car's ~200) were measured for each such item every second —
            // the streamer's once-a-second hitch grew from ~120 to ~300 ms (DevTest, 2026-11-10)
            bool loose = __instance.transform.parent == null;
            if (something && (!Near || !loose || nearest <= Reach(__instance, id, p) + NearM))
            {
                if (_held.Remove(id)) Released++;
                _reach.Remove(id);
                return true;
            }
            if (_held.Count > 4096) _held.Clear();   // ids of items long gone
            float now = Time.realtimeSinceStartup;
            if (!_held.TryGetValue(id, out float since))
            {
                _held[id] = now;
                Held++; if (something) HeldFar++;
                if (Held <= 10) Plugin.Log.LogWarning($"unfreeze held ({(something ? $"what is under it is {nearest:F1} m down" : "nothing under it")} yet): {__instance.name} at global {mainscript.GlobalFromUnityPos(p).ToString()}");
            }
            else if (something && now - since > HoldMaxS)
            {
                // still out of reach: what it stood on may be gone for good; let go as before
                _held.Remove(id); _reach.Remove(id); TimedOut++;
                return true;
            }
            return false;
        }

        /// From the ray's start (0.5 m above the item's position) down to the item's lowest solid collider. A frozen item
        /// doesn't move: measured once while it waits.
        private static readonly System.Collections.Generic.Dictionary<int, float> _reach = new System.Collections.Generic.Dictionary<int, float>();
        private static float Reach(tosaveitemscript it, int id, Vector3 p)
        {
            if (_reach.TryGetValue(id, out float r)) return r;
            _cols.Clear(); it.transform.GetComponentsInChildren(false, _cols);
            float bottom = float.MaxValue;
            foreach (var c in _cols) if (c != null && c.enabled && !c.isTrigger && !(c is WheelCollider)) bottom = System.Math.Min(bottom, c.bounds.min.y);
            r = bottom == float.MaxValue ? 0.5f : System.Math.Max(0f, p.y + 0.5f - bottom);
            if (_reach.Count > 4096) _reach.Clear();
            _reach[id] = r;
            return r;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"near\":" + (Near ? "true" : "false") + ",\"held\":" + Held + ",\"heldFar\":" + HeldFar +
                                      ",\"released\":" + Released + ",\"timedOut\":" + TimedOut + ",\"waiting\":" + _held.Count + "}";
    }
}
