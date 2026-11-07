using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    // Test tooling for held-item lag (bridge `mp fakeinput`, `mp myhands`): mouse input the game reads as if the player
    // gave it, and the local player's hand targets.
    public static partial class Entities
    {
        // Test (bridge `mp fakeinput mmb rmb lookX lookY scroll seconds`): mouse input the game reads as if the player gave it
        // (turn, rotate or move a held item with the middle/right button, scroll), for a while.
        private static bool _fakeMmb, _fakeRmb; private static float _fakeX, _fakeY, _fakeScroll, _fakeUntil;
        internal static string FakeInput(string[] a)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            _fakeMmb = a[1] == "1"; _fakeRmb = a[2] == "1";
            _fakeX = float.Parse(a[3], ic); _fakeY = float.Parse(a[4], ic); _fakeScroll = float.Parse(a[5], ic);
            _fakeUntil = Time.unscaledTime + float.Parse(a[6], ic);
            return "{\"ok\":true}";
        }
        private static void FakeInputApply(inputscript input)
        {
            if (input == null || Time.unscaledTime >= _fakeUntil) return;
            input.mmb = _fakeMmb; input.rmb = _fakeRmb; input.lookX = _fakeX; input.lookY = _fakeY; input.scroll = _fakeScroll;
        }

        /// Test (`mp myhands`): the local player's hand targets in its body frame and every held item's hand-relative pose.
        internal static string MyHands()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var a = pl != null ? pl.anim : null;
            if (a == null) return "{\"error\":\"no player\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string V(Vector3 v) => "[" + v.x.ToString("F3", ic) + "," + v.y.ToString("F3", ic) + "," + v.z.ToString("F3", ic) + "]";
            var T = a.T != null ? a.T : a.transform;
            return "{\"rh\":" + V(a.RHTarget.position) + ",\"lh\":" + V(a.LHTarget.position) + ",\"rhLocal\":" + V(T.InverseTransformPoint(a.RHTarget.position)) +
                   ",\"lhLocal\":" + V(T.InverseTransformPoint(a.LHTarget.position)) + ",\"tYaw\":" + T.eulerAngles.y.ToString("F2", ic) +
                   ",\"tEuler\":" + V(T.eulerAngles) + ",\"t\":" + V(T.position) + ",\"body\":" + V(pl.transform.position) +
                   ",\"yawSent\":" + Mp.LocalYaw().ToString("F2", ic) + ",\"rhAim\":" + Json.Str(pl.VCurrentRHT != null ? pl.VCurrentRHT.name : "") + "}";
        }

        /// `mp itemcheck <net> grab`: the point of the item's colliders nearest the local camera.
        private static bool GrabPoint(Ent e, out Vector3 at)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var cam = pl != null && pl.Cam != null ? pl.Cam.transform : null;
            at = e.Root != null ? e.Root.transform.position : default;
            if (cam == null || e.Root == null) return false;
            float best = float.MaxValue;
            foreach (var c in e.Root.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                var q = c.ClosestPoint(cam.position); float d = (q - cam.position).sqrMagnitude;
                if (d < best) { best = d; at = q; }
            }
            return true;
        }

        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class FakeInputPatch
        {
            [HarmonyPostfix]
            private static void Postfix(inputscript __instance) => FakeInputApply(__instance);
        }
    }
}
