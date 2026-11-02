using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// An edit the server refused is already on its sender's screen, and the owner's record hasn't changed, so its next
        /// resync skips it, and the census doesn't compare a knob's or a door's value: the two stayed different until the owner
        /// moved it. The server asks the owner for that record again, after the sender's EditHold (a record inside it is ignored).
        public static bool RefusedEditsComeBack = true;   // A/B: false = before
        public static long RefusedEditsAsked, RefusedEditsResent;
        /// The same hole for an edit that went through: a record that reaches the sender inside its EditHold is ignored, and the
        /// owner counts it as sent. The owner changing another knob while a copy's edit is on its way left that knob different
        /// for good (aud-fight, 2 of 10 rounds; --only aud-held). So every edit gets one record after its sender's hold.
        public static bool HeldRecordsComeBack = true;   // A/B: false = before
        private const int EditBackMax = 256;
        private static readonly Dictionary<ulong, float> _editBack = new Dictionary<ulong, float>();
        private static readonly List<ulong> _editBackDue = new List<ulong>();

        private static void EditBackLater(SEnt se, int idx, bool malformed)
        {
            if (!RefusedEditsComeBack || malformed || idx < 0) return;
            EditBackAfterHold(se, idx);
        }

        private static void EditBackAfterHold(SEnt se, int idx)
        {
            ulong key = (ulong)se.NetId << 32 | (uint)idx;
            if (_editBack.Count < EditBackMax || _editBack.ContainsKey(key)) _editBack[key] = Time.realtimeSinceStartup + EditHold + 0.2f;
        }

        internal static void EditBackTick()
        {
            if (_editBack.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            _editBackDue.Clear();
            foreach (var kv in _editBack) if (now >= kv.Value) _editBackDue.Add(kv.Key);
            foreach (ulong key in _editBackDue)
            {
                _editBack.Remove(key);
                if (!Server.TryGetValue((uint)(key >> 32), out var se)) continue;
                WS.Reset(); WS.U8(CensusRepair); WS.U32(se.NetId); WS.U32(se.Epoch); WS.U8(8);
                WS.VarU32(1); WS.VarU32((uint)key);
                WS.VarU32(0);
                ServerSendTo(se.OwnerId, WS, true);
                RefusedEditsAsked++;
            }
        }
    }
}
