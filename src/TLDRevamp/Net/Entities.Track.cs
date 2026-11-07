using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    // Test (bridge `mp track <net> <seconds>` then `mp track get`): an item's shown position every frame on this machine,
    // stamped with the wall clock, so two copies on one PC can be compared at the same moment (held-item lag tests).
    internal sealed class TrackRender : MonoBehaviour
    {
        private void LateUpdate() => Entities.TrackLate();
    }

    public static partial class Entities
    {
        private static uint _trackNet; private static float _trackUntil; private static TrackRender _trackGo;
        private static readonly List<string> _trackRows = new List<string>();

        internal static string Track(string[] a)
        {
            if (a.Length > 1 && a[1] == "get") return "{\"rows\":[" + string.Join(",", _trackRows) + "]}";
            _trackNet = uint.Parse(a[1]);
            _trackUntil = Time.unscaledTime + float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
            _trackRows.Clear();
            if (_trackGo == null) { var go = new GameObject("RevampTrack"); Object.DontDestroyOnLoad(go); _trackGo = go.AddComponent<TrackRender>(); }
            return "{\"ok\":true}";
        }

        internal static void TrackLate()
        {
            if (_trackNet == 0 || Time.unscaledTime > _trackUntil || !ByNet.TryGetValue(_trackNet, out var e) || e.Root == null) return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var g = mainscript.GlobalFromUnityPos(e.Root.transform.position);
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var hand = ItemHand(e);
            _trackRows.Add("[" + (System.DateTime.UtcNow.Ticks / 10000.0).ToString("F1", ic) + "," + g.x.ToString("F3", ic) + "," + g.y.ToString("F3", ic) + "," + g.z.ToString("F3", ic) +
                           "," + (hand != null ? 1 : 0) + "," + (e.Ip != null ? e.Ip.DelayMs.ToString("F0", ic) : "0") + "]");
        }
    }
}
