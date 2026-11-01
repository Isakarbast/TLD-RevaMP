using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static void SendPartOff(Ent e, int idx, tosaveitemscript ts)
        {
            var t = ts.transform; var g = mainscript.GlobalFromUnityPos(t.position); var q = t.rotation;
            W.Reset(); W.U8(PartOff); W.U32(e.NetId); W.U32(e.Epoch); W.VarU32((uint)idx);
            W.F64(g.x); W.F64(g.y); W.F64(g.z); W.F32(q.x); W.F32(q.y); W.F32(q.z); W.F32(q.w);
            ToServer(W, true);
            PartsReported++;
            SessionLog.Event("part-off-sent", e.NetId, item: idx, from: MyId, epoch: e.Epoch);
        }

        // a part on a member carried away from its group: Entities.PartOff
        public static long CarriedOffSent, CarriedOffApplied;
        private static readonly Dictionary<(uint, int), tosaveitemscript> _carriedOff = new Dictionary<(uint, int), tosaveitemscript>();
        private static Ent _detachCarrier;
        private static int _detachCarrierSlot;

        private static bool CarriedFar(attachablescript at, out Ent carrier)
        {
            carrier = null;
            if (!CarriedPartsShared || at == null || at.tosave == null || at.slot == null || at.slot.tosaveitem == null || OwnEntity(at.tosave) != null) return false;
            var group = EditEntityOf(at.tosave);
            if (group != null && group.Root != null) return false;
            carrier = OwnEntity(at.slot.tosaveitem);
            return carrier != null && carrier.ParentNet != 0 && carrier.PartIndex > 0;
        }

        /// Here, the part a PartDetached names by its carrier's slot: the one its sender unbolted, or what that slot holds.
        private static tosaveitemscript CarriedItem(uint carrierNet, int slot, int owner)
        {
            if (owner == MyId && _carriedOff.TryGetValue((carrierNet, slot), out var mine) && mine != null) { _carriedOff.Remove((carrierNet, slot)); return mine; }
            if (!ByNet.TryGetValue(carrierNet, out var c) || !Resolve(c) || c.Root == null || c.Root.partslotscripts == null || slot < 0 || slot >= c.Root.partslotscripts.Count) return null;
            var s = c.Root.partslotscripts[slot];
            var part = s != null ? s.part() : null;
            return part != null ? part.tosave : null;
        }

        private static void SendCarriedOff(tosaveitemscript ts, Ent carrier, int slot)
        {
            if (slot < 0) return;
            var t = ts.transform; var g = mainscript.GlobalFromUnityPos(t.position); var q = t.rotation;
            if (_carriedOff.Count > 64) _carriedOff.Clear();
            _carriedOff[(carrier.NetId, slot)] = ts;
            W.Reset(); W.U8(PartOff); W.U32(carrier.NetId); W.U32(carrier.Epoch); W.VarU32(0u);
            W.F64(g.x); W.F64(g.y); W.F64(g.z); W.F32(q.x); W.F32(q.y); W.F32(q.z); W.F32(q.w);
            W.VarU32((uint)slot);
            ToServer(W, true);
            CarriedOffSent++;
            SessionLog.Event("part-off-sent", carrier.NetId, item: slot, from: MyId, epoch: carrier.Epoch, result: "carried");
        }

        [HarmonyLib.HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class DetachHook
        {
            /// Display copies never lose a part on their own — only when the owner reports it.
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(attachablescript __instance)
            {
                if (!HitchDetach(__instance)) return false;
                // the slot is gone after the Detach
                _detachCarrier = !_applyingDetach && InSession && CarriedFar(__instance, out var carrier) ? carrier : null;
                _detachCarrierSlot = _detachCarrier != null && _detachCarrier.Root != null && _detachCarrier.Root.partslotscripts != null ? _detachCarrier.Root.partslotscripts.IndexOf(__instance.slot) : -1;
                if (_applyingDetach || !PartIsProxy(__instance)) return true;
                // a non-owner is taking something off someone else's car: forward the request to the owner, whose
                // real detach flows through the PartOff broadcast. Suppress the local proxy detach.
                // Only what this machine's PLAYER does (PlayerActing): the game also detaches on its own — a hinged
                // part's joint check (attachablescript.Update), loaders replaying a resync — and on a copy each of those
                // went out as a request: the owner's rear seat and door came off six times in 30 s with nobody touching
                // them, wheels bolted on came off again (bugs 3/6/7, 2026-10-03). The owner alone decides those.
                if (InSession && PlayerActing) RequestDetach(__instance);
                else if (InSession) AutoDetachesIgnored++;
                SuppressedFallOff++;
                return false;
            }

            /// The owner lost a part (crash or wrench): report which one.
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(attachablescript __instance, bool __runOriginal = true)
            {
                if (InSession && !__instance.attached) LiveUsablesChanged(__instance.tosave);
                if (!__runOriginal || _applyingDetach || !InSession) return;
                var ts = __instance.tosave;
                // before PartIsProxy: its slot is gone now, so a copy of the far group would answer for it
                if (_detachCarrier != null) { if (ts != null) SendCarriedOff(ts, _detachCarrier, _detachCarrierSlot); _detachCarrier = null; return; }
                if (PartIsProxy(__instance) || ts == null) return;
                // its own shared object (spawned loose, or a part that came off before and was bolted back on): it
                // comes off everyone's copy — a PartOff would make a second entity for the same item
                var own = OwnEntity(ts);
                if (own != null) { if (!own.Proxy) SendDetachSync(own); return; }
                var group = EditEntityOf(ts);
                foreach (var e in ByNet.Values)
                {
                    if (e != group || e.Proxy) continue;
                    int idx = e.Items.IndexOf(ts);
                    if (idx < 0) continue;
                    SendPartOff(e, idx, ts);
                    return;
                }
            }
        }

        /// A bolted car part comes off through partslotscript.UnCraft (the wrench / E-press dismount, a crash's
        /// FallOFf): it un-bolts first (crafted=false, colliders back on, pickable layer, the slot's installed mesh,
        /// mass, wheel-lock size) and only then calls Detach. On a display copy that un-bolt must not run half — the
        /// Detach at its end is suppressed, leaving a loose-looking part that still counts as bolted. Forward the
        /// request instead; the owner's PartOff takes it off here the same way.
        [HarmonyLib.HarmonyPatch(typeof(partslotscript), nameof(partslotscript.UnCraft), new[] { typeof(bool) })]
        private static class ProxyUnCraft
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(partslotscript __instance, ref bool __result)
            {
                if (_applyingDetach || !InSession) return true;
                var part = __instance.part();
                if (!HitchDetach(part)) { __result = false; return false; }
                if (part == null || !PartIsProxy(part)) return true;
                if (PlayerActing) { if (RequestDetach(part)) { _handFor = part.tosave; _handAt = Time.realtimeSinceStartup; } }
                else AutoDetachesIgnored++;
                SuppressedFallOff++;
                __result = false;   // the game's dismount then doesn't put a still-bolted part in the player's hands
                return false;
            }
        }

        /// Take a part off the way the game does: a slotted part is un-bolted via its slot (the official MP's
        /// receiver does the same, syncScript.RecAttachable); anything else is a plain Detach.
        internal static bool Unmount(attachablescript at, bool fallOff)
        {
            if (at.slot != null && at.slot.part() == at) return at.slot.UnCraft(fallOff);
            at.Detach();
            return true;
        }

        /// The owner runs the real detach; its DetachHook postfix broadcasts the PartOff to everyone.
        public static long DetachReqsApplied;
        internal static void ApplyDetachReq(int from, uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Proxy) return;
            var it = idx >= 0 && idx < e.Items.Count ? e.Items[idx] : idx == 0 ? e.Root : null;
            if (it == null) return;
            var at = it.attachable;
            if (at == null || !at.attached) return;
            // the game's own dismount (un-bolt, then Detach): DetachHook's postfix reports the PartOff to everyone
            if (Unmount(at, false)) DetachReqsApplied++;
        }
        /// The simulating machine took its own shared object off what it was attached to (a crash, the wrench, a
        /// player's grab): everyone's copy comes off the same way and follows its pose stream again.
        private static void SendDetachSync(Ent pe)
        {
            var t = pe.Root.transform; var g = mainscript.GlobalFromUnityPos(t.position); var q = t.rotation;
            W.Reset(); W.U8(DetachSync); W.U32(pe.NetId); W.U32(pe.Epoch);
            W.F64(g.x); W.F64(g.y); W.F64(g.z); W.F32(q.x); W.F32(q.y); W.F32(q.z); W.F32(q.w);
            ToServer(W, true);
            pe.SentAtRest = false;
            DetachSyncsSent++;
            SessionLog.Event("detach-sent", pe.NetId, pe.ParentNet, item: pe.PartIndex, from: MyId, epoch: pe.Epoch);
        }

        internal static void ApplyDetachSync(NetReader r)
        {
            uint net = r.U32(); r.U32();
            var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            if (r.Bad || !ByNet.TryGetValue(net, out var pe)) return;
            if (!Resolve(pe) || pe.Root == null)
            {
                // our copy is in the far store: it comes off there (placed back loose where it landed)
                if (StoredAttach && DetachStored(pe.RootId, pos, rot)) { DetachSyncsApplied++; DetachesStored++; SessionLog.Event("detach-applied", net, to: MyId, result: "far store"); }
                return;
            }
            var at = pe.Root.attachable;
            if (at != null && at.attached)
            {
                _applyingDetach = true;
                try { Unmount(at, true); } finally { _applyingDetach = false; }
            }
            pe.Root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
            if (pe.Proxy) { ApplyProxyBodies(pe); pe.Ip = new PoseInterpolator(); }
            DetachSyncsApplied++;
            HandOver(pe.Root);
            SessionLog.Event("detach-applied", net, pe.ParentNet, item: pe.PartIndex, to: MyId, epoch: pe.Epoch);
        }

    }
}
