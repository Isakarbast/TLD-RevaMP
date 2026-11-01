using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Where an object is, checked like its parts: a copy near its player adds its position to the census ask (PlaceDigest:
    /// six bytes, the question it was asking anyway), and the owner says nothing unless the copy is really somewhere else.
    /// A state at rest is sent once: when the last one is lost or refused, or a copy stops following them, the copy stays
    /// where it was for good and neither machine sees anything wrong (an oil can 60 m off in a random-action test run). Only an owner's
    /// item that isn't moving fast is compared: copies of moving things are a little late by design.
    public static partial class Entities
    {
        public static bool CensusPlaces = true;   // A/B: false = the census only compares parts and state (before)
        public static float RepairPlaceM = 0.5f, RepairPlaceSpeed = 1f;
        public static long PlacesAsked, PlacesApart, PlacesResent, PlacesTold, PlacesHealed, PlacesFailed;
        public static uint TestDropStates;   // test: this copy doesn't hear this object's states (a lost final state)
        private const byte PlaceTold = 200;   // CensusReply's attempt byte: not rows but what the owner found
        private static readonly Dictionary<uint, float> _placeRunAt = new Dictionary<uint, float>();
        private struct PlaceWait { public float Until; public int Told; }
        private static readonly Dictionary<uint, PlaceWait> _placeWait = new Dictionary<uint, PlaceWait>();
        private static readonly List<uint> _placeDone = new List<uint>();

        /// Placed by something other than its own position on both machines: not compared.
        private static bool PlaceFollowsElse(Ent e) =>
            e.Root == null || e.Root.nyul != null || e.Root.npc != null || e.Root.ragdollPos != null || e.Rag != null || CompactFlyer(e) || e.HitchWaiting ||
            e.Root.attachable != null && e.Root.attachable.attached || RidesBolted(e) || AttachedCarPart(e);

        /// Copy, the ask: its position when it has one to compare.
        private static void WriteCopyPlace(Ent e, byte attempt)
        {
            if (!CensusPlaces || attempt != 1 || e.Held || e.Stored || WornRides(e) || e.ReleaseGhost || HandManaged(e) || PlaceFollowsElse(e)) return;
            var g = mainscript.GlobalFromUnityPos(e.Root.transform.position);
            W.U16(PlaceDigest.Of(g.x)); W.U16(PlaceDigest.Of(g.y)); W.U16(PlaceDigest.Of(g.z));
            PlacesAsked++;
        }

        /// Owner: this one isn't on its way anywhere, in a body or in the thing that carries it.
        private static bool OwnerPlaceStill(Ent e)
        {
            var t = e.Root.transform;
            var rb = e.Root.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) rb = t.parent != null ? t.parent.GetComponentInParent<Rigidbody>() : null;
            if (rb == null) return true;
            if (!rb.isKinematic) return rb.velocity.sqrMagnitude <= RepairPlaceSpeed * RepairPlaceSpeed;
            var carrier = OwnEntity(rb.GetComponent<tosaveitemscript>());   // someone else's car, our copy of it reads 0 km/h
            return carrier == null || carrier.LastVel.sqrMagnitude <= RepairPlaceSpeed * RepairPlaceSpeed;
        }

        /// Owner: the copy's position against ours. Different: one state again (what `first` sends: reliable when at rest), and
        /// the asker is told what we see, for its log.
        private static void CheckCopyPlace(Ent e, int asker, ushort x, ushort y, ushort z)
        {
            if (!CensusPlaces || IsHeld(e.Root) || IsStored(e.Root) || PlaceFollowsElse(e) || !OwnerPlaceStill(e)) return;
            var g = mainscript.GlobalFromUnityPos(e.Root.transform.position);
            double apart = PlaceDigest.Apart(PlaceDigest.Of(g.x), PlaceDigest.Of(g.y), PlaceDigest.Of(g.z), x, y, z);
            if (apart < RepairPlaceM) return;
            PlacesApart++;
            float now = Time.realtimeSinceStartup;
            if (_placeRunAt.TryGetValue(e.NetId, out float at) && now - at < RepairRunEveryS) return;
            if (_placeRunAt.Count > 512) _placeRunAt.Clear();
            _placeRunAt[e.NetId] = now;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rb = e.Root.GetComponent<Rigidbody>();
            string facts = apart.ToString("F1", ic) + " m, owner at " + g.x.ToString("F1", ic) + " " + g.y.ToString("F1", ic) + " " + g.z.ToString("F1", ic) +
                           ", at rest " + e.SentAtRest + ", kinematic " + (rb != null && rb.isKinematic) + ", last state " + (Time.realtimeSinceStartupAsDouble - e.LastStateAt).ToString("F0", ic) + " s ago";
            e.LastStateEpoch = 0; e.SentAtRest = false;   // Entities.cs: `first`, one fresh state
            PlacesResent++;
            SessionLog.Event("place-repair", e.NetId, from: asker, to: MyId, result: facts);
            W.Reset(); W.U8(CensusReply); W.U32(e.NetId); W.VarU32((uint)asker); W.U8(PlaceTold); W.Str(facts);
            ToServer(W, true);
        }

        /// Copy: the owner found this copy somewhere else and sent a state. That state can be lost too, so a second time is one more
        /// try; a third, with the states having had time to arrive, means states don't help.
        private static void CopyPlaceHeard(Ent e, string facts)
        {
            float now = Time.realtimeSinceStartup;
            PlacesTold++;
            var rb = e.Root.GetComponent<Rigidbody>();
            string here = facts + "; copy: physical " + e.Physical + ", moved by body " + e.MovedByBody + ", kinematic " + (rb != null && rb.isKinematic) + ", locked " + e.PoseLocked +
                          ", away " + LockedAway(e) + ", rides " + RidesOwnersContainer(e) + ", carried by " + e.CarriedBy + ", interpolating " + (e.Ip != null && e.Ip.Ready) + ", at rest " + e.ReceivedAtRest + ", states " + e.Recv + ", in our hand " + (mainscript.s != null && mainscript.s.player != null && mainscript.s.player.pickedUp != null && mainscript.s.player.pickedUp == e.Root.P);
            RepairLine("place of " + e.Root.name + " net " + e.NetId + ": " + here);
            _placeWait.TryGetValue(e.NetId, out var told);
            if (told.Told >= 2)
            {
                PlacesFailed++;
                Anomaly($"place repair did not take: {e.Root.name} net {e.NetId} owner {e.OwnerId} — {here}");
                float back = _repairWait.TryGetValue(e.NetId, out var was) && was.Backoff > 0 ? Mathf.Min(was.Backoff * 2f, RepairBackoffMaxS) : RepairBackoffS;
                _repairWait[e.NetId] = new RepairWait { Until = now + back, Backoff = back };
                _placeWait.Remove(e.NetId);
                return;
            }
            _placeWait[e.NetId] = new PlaceWait { Until = now + RepairVerifyS + CensusRecheckS, Told = told.Told + 1 };
            _censusDue[e.NetId] = new CensusDue { At = now + RepairVerifyS, Attempt = 1 };
        }

        private static void PlaceWaitTick(float now)
        {
            if (_placeWait.Count == 0) return;
            _placeDone.Clear();
            foreach (var kv in _placeWait) if (now >= kv.Value.Until) _placeDone.Add(kv.Key);
            foreach (var net in _placeDone)
            {
                _placeWait.Remove(net);
                PlacesHealed++;
                RepairLine("place healed: net " + net);
            }
        }

        /// `mp places`: the counters and what a census ask added.
        public static string PlacesJson() =>
            "{\"asked\":" + PlacesAsked + ",\"apart\":" + PlacesApart + ",\"resent\":" + PlacesResent + ",\"told\":" + PlacesTold + ",\"healed\":" + PlacesHealed + ",\"failed\":" + PlacesFailed +
            ",\"asks\":" + CensusAsks + ",\"addedBytes\":" + PlacesAsked * PlaceDigest.Bytes + "}";
    }
}
