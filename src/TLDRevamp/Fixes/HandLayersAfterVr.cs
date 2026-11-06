using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's VR switch (mainscript.SetVR, which the settings refresh calls with false outside VR too) puts every
    /// turnable usable's collider back on the layer it had when the usable started (usablescript.UpdVR: col.layer =
    /// defLayer). A thing in the player's hand had its colliders moved to the hand's layer (pickupable.changeLayer:
    /// RHLayer in the right hand, pickedUpLayer held in front), which the car body doesn't touch; its switch collider was
    /// back on a layer that does. Seated, the hand's thing is a kinematic body set by transform once a frame while the car
    /// moves in the physics steps between: past ~60 km/h the car body reached it, and the crash stopped the car dead and
    /// knocked its parts off (playtest 2026-11-06: a lamp in the right hand, the settings applied once, then three crashes
    /// at 60 km/h on a flat road, each against the car's own kinematic collider; re-gripping the lamp ended it).
    /// Afterwards the hands' things get their hand layers again, as the game gives them when they're taken in hand.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.HandLayersAfterVr.Enabled
    [HarmonyPatch(typeof(mainscript), nameof(mainscript.SetVR))]
    public static class HandLayersAfterVr
    {
        public static bool Enabled = true;
        public static long Restored;

        [HarmonyPostfix]
        private static void Postfix(bool _enable)
        {
            if (!Enabled || _enable || mainscript.s == null) return;
            var pl = mainscript.s.player;
            if (pl == null) return;
            if (pl.inHandP != null) { pl.inHandP.changeLayer(mainscript.s.RHLayer); Restored++; }
            if (pl.pickedUp != null) { pl.pickedUp.changeLayer(mainscript.pickedUpLayer); Restored++; }
        }
    
        /// Diagnostics (bridge `handlayers`): every collider of what the player holds, its layer, whether it's a trigger,
        /// and the layers the hands should give them.
        public static string Json()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            string Of(pickupable p)
            {
                if (p == null) return "null";
                var rows = new System.Collections.Generic.List<string>();
                foreach (var c in p.GetComponentsInChildren<Collider>(true))
                    rows.Add("{\"name\":" + TLDRevamp.Json.Str(c.name) + ",\"layer\":" + c.gameObject.layer + ",\"trigger\":" + (c.isTrigger ? "true" : "false") + ",\"on\":" + (c.enabled ? "true" : "false") + "}");
                return "{\"name\":" + TLDRevamp.Json.Str(p.name) + ",\"colliders\":[" + string.Join(",", rows) + "]}";
            }
            return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"restored\":" + Restored + ",\"rhLayer\":" + mainscript.s.RHLayer + ",\"pickedUpLayer\":" + mainscript.pickedUpLayer +
                   ",\"inHand\":" + Of(pl.inHandP) + ",\"pickedUp\":" + Of(pl.pickedUp) + "}";
        }
    }
}
