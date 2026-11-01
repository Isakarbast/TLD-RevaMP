using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Every one-shot message that was lost, refused or arrived where it could not be applied leaves two machines different
    /// for good: nothing resends it, and the sender's own screen looks right (double doors, a tyre off on one screen, a hat
    /// on one head). Fixing them one message at a time only fixes the ones found, so copies now check themselves. A copy
    /// near its player asks about one object at a time (RepairAskEveryS): a hash of what is bolted where and of the state a
    /// player can change. The owner stays silent when it has the same, and answers with its rows when not. Still different
    /// CensusRecheckS later (not a change on its way), the copy names the difference and the owner sends what it holds again:
    /// the group's records, its own attach or detach, a part-off that never got through. Those messages are the normal ones,
    /// so nothing here needs to know how to bolt a door. Rows use group indices and net ids: the same on every machine.
    public static partial class Entities
    {
        public static bool CensusRepairs = true;   // A/B: false = the census only logs a car's parts when it is placed (before)
        public static float RepairAskEveryS = 0.25f, RepairRangeM = 200f, RepairVerifyS = 8f, RepairBackoffS = 30f, RepairBackoffMaxS = 600f, RepairRunEveryS = 4f;
        public static long RepairsAsked, RepairsHealed, RepairsFailed, RepairsRun, RepairPartsReplayed, RepairPartOffsResent, RepairAttachesResent, RepairDetachesResent, RepairResyncsForced;
        public static double RepairRowsMsMax;
        public static int RepairLogCap = 60;
        private static int _repairLogged;
        private static float _repairSweepAt;
        private static int _repairCursor;
        private static readonly List<uint> _repairCycle = new List<uint>();
        private struct RepairWait { public float Until, Backoff; public string Diff; public bool Verifying; }
        private static readonly Dictionary<uint, RepairWait> _repairWait = new Dictionary<uint, RepairWait>();
        private static readonly Dictionary<uint, float> _repairRunAt = new Dictionary<uint, float>();
        private static readonly List<uint> _repairDone = new List<uint>();

        /// `mp censusnow <net>`: this copy's check now, not when its turn comes.
        public static string CensusNow(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !e.Proxy || !Resolve(e) || e.Root == null) return "{\"error\":\"no copy of it placed here\"}";
            _repairWait.Remove(net);
            _censusDue[net] = new CensusDue { At = Time.realtimeSinceStartup, Attempt = 1 };
            return "{\"rows\":" + RepairRows(e).Count + "}";
        }

        /// `mp repairrows <net>`: the rows this machine would hash, to compare two machines.
        public static void TryRows(uint net, out string json)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) { json = "{\"error\":\"no such object here\"}"; return; }
            var rows = new List<string>(); foreach (var row in RepairRows(e)) rows.Add(Json.Str(row));
            json = "{\"rows\":[" + string.Join(",", rows) + "],\"msMax\":" + Json.Num((float)RepairRowsMsMax) + ",\"asked\":" + RepairsAsked + ",\"healed\":" + RepairsHealed + ",\"failed\":" + RepairsFailed + ",\"run\":" + RepairsRun + "}";
        }

        private static void RepairLine(string what)
        {
            if (_repairLogged >= RepairLogCap) return;
            _repairLogged++;
            _anomLines.Add("repair: " + what);
            Plugin.Log.LogWarning("[repair] " + what);
        }

        /// Per frame on a copy: the next object's turn, and repairs that heard nothing more (the owner's hash is the same now).
        private static void RepairTick(float now)
        {
            if (!CensusRepairs) return;
            if (_repairWait.Count > 0)
            {
                _repairDone.Clear();
                foreach (var kv in _repairWait) if (now >= kv.Value.Until) _repairDone.Add(kv.Key);
                foreach (var net in _repairDone)
                {
                    var w = _repairWait[net];
                    if (w.Verifying)
                    {
                        RepairsHealed++;
                        SessionLog.Event("census-repair", net, to: MyId, result: "healed");
                        RepairLine("healed net " + net + ": " + w.Diff);
                        _repairWait[net] = new RepairWait { Until = now + RepairBackoffS, Backoff = RepairBackoffS };
                    }
                    else _repairWait.Remove(net);
                }
            }
            if (now - _repairSweepAt < RepairAskEveryS) return;
            _repairSweepAt = now;
            if (_repairCursor >= _repairCycle.Count)
            {
                _repairCycle.Clear(); _repairCursor = 0;
                foreach (var e in ByNet.Values) if (e.Proxy && e.Root != null) _repairCycle.Add(e.NetId);
            }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;
            // loose things far away are skipped in one go, not one per turn: a cycle takes as long as what is near
            for (int tries = 0; tries < 64 && _repairCursor < _repairCycle.Count; tries++)
            {
                uint net = _repairCycle[_repairCursor++];
                if (_censusDue.ContainsKey(net) || _repairWait.ContainsKey(net)) continue;
                if (!ByNet.TryGetValue(net, out var e) || !e.Proxy || e.Root == null) continue;
                if ((e.Root.transform.position - pl.transform.position).sqrMagnitude > RepairRangeM * RepairRangeM) continue;
                _censusDue[net] = new CensusDue { At = now, Attempt = 1 };
                return;
            }
        }

        /// What this machine has for the object, the same text on the owner's and on a copy that agrees: one row per item
        /// (m7=on0#4: member 7 in slot 4 of member 0), its state (s7=...), and where the object itself is bolted (self=...).
        internal static List<string> RepairRows(Ent e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = new List<string>();
            var index = new Dictionary<tosaveitemscript, int>();
            for (int i = 0; i < e.Items.Count; i++) if (e.Items[i] != null) index[e.Items[i]] = i;
            Dictionary<int, uint> parts = null;
            if (e.Split != null && e.Split.Count > 0)
            {
                parts = new Dictionary<int, uint>();
                foreach (var p in ByNet.Values) if (p.ParentNet == e.NetId && p.PartIndex >= 0) parts[p.PartIndex] = p.NetId;
            }
            for (int i = 0; i < e.Items.Count; i++)
            {
                if (e.Split != null && e.Split.Contains(i))
                {
                    rows.Add("m" + i + "=split:" + (parts != null && parts.TryGetValue(i, out uint pn) ? pn : 0u));
                    continue;
                }
                var it = e.Items[i];
                if (it == null) { rows.Add("m" + i + "=none"); continue; }
                string state = RepairState(it);
                if (state.Length > 0) rows.Add("s" + i + "=" + state);
                if (it == e.Root) { rows.Add("self=" + RepairSelf(e, it)); continue; }
                var at = it.attachable;
                if (at == null || at.utanfuto != null) continue;
                if (!at.attached) { rows.Add("m" + i + "=off"); continue; }
                var owner = Fixes.SlotOwnerSave.Owner(at);
                string on = owner != null && index.TryGetValue(owner, out int pi) ? pi.ToString() : "*";
                rows.Add("m" + i + "=" + (at.slot != null && owner != null && owner.partslotscripts != null ? "on" + on + "#" + owner.partslotscripts.IndexOf(at.slot) : "glue" + on));
            }
            rows.Sort(System.StringComparer.Ordinal);
            double ms = sw.Elapsed.TotalMilliseconds; if (ms > RepairRowsMsMax) RepairRowsMsMax = ms;
            return rows;
        }

        /// Whole litres: a running engine moves the level, and the copy hears it up to a resync late.
        private static string RepairState(tosaveitemscript it)
        {
            var sb = new System.Text.StringBuilder();
            if (it.partconditions != null)
                foreach (var pc in it.partconditions) if (pc != null) sb.Append('c').Append(pc.state).Append('.').Append(pc.state2).Append('.').Append(pc.state3);
            if (it.usables != null)
                foreach (var u in it.usables) if (u != null && u.turnable) sb.Append('u').Append(u.currentTurnState);
            if (it.tanks != null)
                foreach (var t in it.tanks) if (t != null && t.F != null) sb.Append('t').Append(Mathf.RoundToInt(t.F.GetAmount()));
            return sb.ToString();
        }

        private static string RepairSelf(Ent e, tosaveitemscript root)
        {
            var at = root.attachable;
            if (at == null) return "-";
            if (at.utanfuto != null) return "hitch";   // a trailer's coupling has its own protocol (Entities.Hitch)
            if (!at.attached) return "off";
            var owner = Fixes.SlotOwnerSave.Owner(at);
            if (owner == null) return "world";
            var pa = OwnEntity(owner);
            int idx = 0;
            if (pa == null)
                foreach (var o in ByNet.Values)
                {
                    if (o == e || o.Root == null) continue;
                    int i = o.Items.IndexOf(owner);
                    if (i >= 0) { pa = o; idx = i + 1; break; }
                }
            string where = pa != null ? pa.NetId + ":" + idx : "*";
            return at.slot != null && owner.partslotscripts != null ? "on" + where + "#" + owner.partslotscripts.IndexOf(at.slot) : "glue" + where;
        }

        private static Dictionary<string, string> RowMap(IEnumerable<string> rows)
        {
            var map = new Dictionary<string, string>();
            foreach (var row in rows)
            {
                int eq = row.IndexOf('=');
                if (eq > 0) map[row.Substring(0, eq)] = row.Substring(eq + 1);
            }
            return map;
        }

        /// A copy: the owner's rows, still different after the recheck (attempt 2), or after the repair (attempt 3).
        private static void RepairReply(Ent e, byte attempt, string theirs)
        {
            float now = Time.realtimeSinceStartup;
            uint net = e.NetId;
            var ours = RowMap(RepairRows(e)); var owner = RowMap(theirs.Split('\n'));
            var diff = new List<string>();
            byte flags = 0;
            var off = new List<int>(); var missing = new List<uint>();
            foreach (var kv in owner)
            {
                ours.TryGetValue(kv.Key, out string mine);
                if (mine == kv.Value) continue;
                diff.Add(kv.Key + " " + (mine ?? "?") + " here, " + kv.Value + " on the owner");
                if (kv.Key == "self") { flags |= 1; continue; }
                if (kv.Key[0] == 'm' && kv.Value.StartsWith("split:"))
                {
                    if (uint.TryParse(kv.Value.Substring(6), out uint pn) && pn != 0 && !ByNet.ContainsKey(pn)) missing.Add(pn);
                    continue;
                }
                if (kv.Key[0] == 'm' && kv.Value == "off" && mine != null && mine != "none" && int.TryParse(kv.Key.Substring(1), out int i)) { off.Add(i); continue; }
                flags |= 2;
            }
            foreach (var kv in ours) if (!owner.ContainsKey(kv.Key)) { diff.Add(kv.Key + " " + kv.Value + " here, nothing on the owner"); flags |= 2; }
            string text = string.Join("; ", diff);
            if (attempt >= 3)
            {
                float back = _repairWait.TryGetValue(net, out var was) && was.Backoff > 0 ? Mathf.Min(was.Backoff * 2f, RepairBackoffMaxS) : RepairBackoffS;
                RepairsFailed++;
                SessionLog.Event("census-repair-failed", net, to: MyId, result: text.Length > 200 ? text.Substring(0, 200) : text);
                Anomaly($"repair did not take: {e.Root.name} net {net} owner {e.OwnerId} — {text}");
                _repairWait[net] = new RepairWait { Until = now + back, Backoff = back };
                return;
            }
            W.Reset(); W.U8(CensusRepair); W.U32(net); W.U32(e.Epoch); W.U8(flags);
            W.VarU32((uint)off.Count); foreach (int i in off) W.VarU32((uint)i);
            W.VarU32((uint)missing.Count); foreach (uint pn in missing) W.U32(pn);
            ToServer(W, true);
            RepairsAsked++;
            SessionLog.Event("census-repair", net, to: e.OwnerId, epoch: e.Epoch, result: "asked: " + (text.Length > 200 ? text.Substring(0, 200) : text));
            // heard nothing by the end of this wait: it is the same now (RepairTick). A reply to the verify lands here as attempt 3.
            _repairWait[net] = new RepairWait { Until = now + RepairVerifyS + CensusRecheckS, Verifying = true, Diff = text, Backoff = RepairBackoffS };
            _censusDue[net] = new CensusDue { At = now + RepairVerifyS, Attempt = 3 };
        }

        /// Server: parts the asker never heard of come from here (their first message was lost to it: it had no such car then),
        /// the rest from the owner, who is the only one who knows what it has now.
        internal static void ServerCensusRepair(int from, NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(); byte flags = r.U8();
            var off = new List<int>();
            uint n = r.VarU32();
            for (uint k = 0; k < n && k < 4096 && !r.Bad; k++) off.Add((int)r.VarU32());
            uint m = r.VarU32();
            var missing = new List<uint>();
            for (uint k = 0; k < m && k < 4096 && !r.Bad; k++) missing.Add(r.U32());
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from || se.Epoch != epoch) return;
            foreach (uint pn in missing)
            {
                if (!Server.TryGetValue(pn, out var part) || part.ParentNet != net) continue;
                WritePartDetached(W, part);
                ServerSendTo(from, W, true);
                KnownBy(from).Add(pn);
                RepairPartsReplayed++;
            }
            if (flags == 0 && off.Count == 0) return;
            if (off.Count > 0) { if (se.RepairOff == null) se.RepairOff = new HashSet<int>(); foreach (int i in off) se.RepairOff.Add(i); }
            WS.Reset(); WS.U8(CensusRepair); WS.U32(net); WS.U32(epoch); WS.U8(flags);
            WS.VarU32((uint)off.Count); foreach (int i in off) WS.VarU32((uint)i);
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// Owner: says again what it holds. Each message is one the normal paths already apply, and applies twice harmlessly.
        internal static void ApplyCensusRepair(NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(); byte flags = r.U8();
            var off = new List<int>();
            uint n = r.VarU32();
            for (uint k = 0; k < n && k < 4096 && !r.Bad; k++) off.Add((int)r.VarU32());
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || e.Proxy || e.Epoch != epoch || !Resolve(e) || e.Root == null) return;
            float now = Time.realtimeSinceStartup;
            if (_repairRunAt.TryGetValue(net, out float at) && now - at < RepairRunEveryS) return;
            if (_repairRunAt.Count > 512) _repairRunAt.Clear();
            _repairRunAt[net] = now;
            RepairsRun++;
            SessionLog.Event("census-repair-run", net, from: MyId, epoch: e.Epoch, count: off.Count, slot: flags);
            if ((flags & 2) != 0)
            {
                e.ResyncHashes = null; e.ResyncCursor = -1; e.ResyncAcc = ResyncInterval;   // every record out on the next pass
                RepairResyncsForced++;
            }
            if ((flags & 1) != 0 && e.Root.attachable != null && e.Root.attachable.utanfuto == null)
            {
                if (e.Root.attachable.attached) { SendAttach(e, e.Root.attachable); RepairAttachesResent++; }
                else { SendDetachSync(e); RepairDetachesResent++; }
            }
            foreach (int i in off)
            {
                var it = i > 0 && i < e.Items.Count && (e.Split == null || !e.Split.Contains(i)) ? e.Items[i] : null;
                if (it == null || it.attachable == null || it.attachable.attached) continue;
                SendPartOff(e, i, it);
                RepairPartOffsResent++;
            }
        }
    }
}
