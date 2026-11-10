using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game unfreezes our player on the world's first frame (fpscontroller.Update, once the map's firstRun is set),
    /// before its terrain tiles exist: terrainGenScript makes them a few at a time over the next seconds (on a thread, one
    /// placed every other frame). A player saved standing then fell through where the ground was about to be — the DevTest
    /// gas station (2026-11-10): ~600 m down, at times ("the gas station ground doesn't load"). Joining has its own hold
    /// (Net.JoinLoading); this is loading a world here, solo or to host.
    ///
    /// After a world loads, our player stays frozen until something solid is under the feet (a ray down, GroundM), at most
    /// HoldMaxS. Runtime switch (bridge `set`): TLDRevamp.Fixes.LoadGroundHold.Enabled
    [HarmonyPatch(typeof(fpscontroller), "Update")]
    public static class LoadGroundHold
    {
        public static bool Enabled = true;
        public static float GroundM = 3f, HoldMaxS = 60f;
        public static long Holds, Released, TimedOut;
        private static mapSettingScript _map;
        private static float _since;
        private static bool _done = true, _holding;
        private static readonly RaycastHit[] _hits = new RaycastHit[16];

        [HarmonyPostfix]
        private static void Postfix(fpscontroller __instance)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != _map) { _map = map; _done = false; _holding = false; _since = Time.realtimeSinceStartup; }
            if (_done || map == null || !map.firstRun) return;   // before firstRun the game holds the player itself
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (!Enabled || pl == null || __instance != pl || pl.RB == null || pl.Bsitting || Net.JoinLoading.Showing || Net.Mp.Client != null)
            {
                End();
                return;
            }
            if (Ground(pl)) { if (_holding) Released++; End(); return; }
            if (Time.realtimeSinceStartup - _since > HoldMaxS)
            {
                TimedOut++;
                Plugin.Log.LogWarning($"load ground hold: still nothing under the player after {HoldMaxS:F0} s at {mainscript.GlobalFromUnityPos(pl.transform.position)}: let go");
                End();
                return;
            }
            if (!_holding)
            {
                _holding = true; Holds++;
                Plugin.Log.LogInfo($"load ground hold: nothing under the player yet at {mainscript.GlobalFromUnityPos(pl.transform.position)}: held until the ground is there");
            }
            pl.RB.isKinematic = true;
        }

        private static void End()
        {
            if (_holding) Plugin.Log.LogInfo($"load ground hold: let go after {Time.realtimeSinceStartup - _since:F1} s");
            _done = true; _holding = false;
        }

        private static bool Ground(fpscontroller pl)
        {
            var root = pl.transform.root;
            int n = Physics.RaycastNonAlloc(pl.transform.position + Vector3.up * 0.5f, Vector3.down, _hits, GroundM + 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (_hits[i].collider != null && _hits[i].collider.transform.root != root) return true;
            return false;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"holds\":" + Holds + ",\"released\":" + Released + ",\"timedOut\":" + TimedOut + ",\"holding\":" + (_holding ? "true" : "false") + "}";
    }
}
