// A refused edit: the server asks the owner for that record again, once, after the sender's hold.
using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;

namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        const float EditHold = 1.5f;
        const byte CensusRepair = 63;
        sealed class SEnt { public uint NetId, Epoch; public int OwnerId; }
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly NetWriter WS = new NetWriter();
        static readonly List<(int to, byte[] msg, bool reliable)> Sent = new List<(int, byte[], bool)>();
        static void ServerSendTo(int to, NetWriter w, bool reliable)
        {
            var b = new byte[w.Len]; Array.Copy(w.Buf, b, w.Len); Sent.Add((to, b, reliable));
        }

        static NetReader Read(byte[] b) { var r = new NetReader(); r.Buf = b; r.Pos = 0; r.End = b.Length; return r; }

        internal static int Run()
        {
            int fails = 0, checks = 0;
            void check(bool ok, string what) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
            var car = new SEnt { NetId = 7, Epoch = 3, OwnerId = 0 };
            Server[7] = car;
            float t = 100; Time.realtimeSinceStartup = t;

            EditBackLater(car, 18, false);
            EditBackTick();
            check(Sent.Count == 0, "nothing inside the sender's hold: its game would ignore the record");
            Time.realtimeSinceStartup = t + EditHold - 0.01f; EditBackTick();
            check(Sent.Count == 0, "still nothing just before the hold ends");
            Time.realtimeSinceStartup = t + EditHold + 0.21f; EditBackTick();
            check(Sent.Count == 1 && Sent[0].to == 0 && Sent[0].reliable, "one reliable message to the owner after the hold");
            var r = Read(Sent[0].msg);
            check(r.U8() == CensusRepair && r.U32() == 7 && r.U32() == 3 && r.U8() == 8, "a repair of this object and epoch, flag 8");
            check(r.VarU32() == 1 && r.VarU32() == 18, "names the one refused item");
            check(r.VarU32() == 0 && !r.Bad && r.Remaining == 0, "the rest of the repair message: reads as the owner reads it");
            EditBackTick();
            check(Sent.Count == 1 && RefusedEditsAsked == 1, "asked once");

            // an edit that went through: the same one record after the sender's hold
            Sent.Clear(); t = 150; Time.realtimeSinceStartup = t;
            EditBackAfterHold(car, 18); EditBackTick();
            check(Sent.Count == 0, "accepted edit: nothing inside the hold");
            EditBackAfterHold(car, 18);
            Time.realtimeSinceStartup = t + EditHold + 0.21f; EditBackTick();
            check(Sent.Count == 1, "accepted edit: one record after the hold");
            EditBackTick();
            check(Sent.Count == 1, "accepted edit: once");

            // a drag out of reach: a refused edit after another, one ask after the last one's hold
            Sent.Clear(); t = 200; Time.realtimeSinceStartup = t;
            for (int k = 0; k < 5; k++) { Time.realtimeSinceStartup = t + k; EditBackLater(car, 2, false); EditBackTick(); }
            check(Sent.Count == 0, "a drag keeps its hold going");
            Time.realtimeSinceStartup = t + 4 + EditHold + 0.21f; EditBackTick();
            check(Sent.Count == 1, "one ask at the end of the drag");

            // the owner changed while it waited: the current owner and epoch
            Sent.Clear(); t = 300; Time.realtimeSinceStartup = t;
            EditBackLater(car, 4, false); car.OwnerId = 2; car.Epoch = 5;
            Time.realtimeSinceStartup = t + 2; EditBackTick();
            r = Read(Sent[0].msg); r.U8(); r.U32();
            check(Sent[0].to == 2 && r.U32() == 5, "goes to whoever owns it now, with their epoch");

            // the object went away
            Sent.Clear(); t = 400; Time.realtimeSinceStartup = t;
            var gone = new SEnt { NetId = 9, Epoch = 1 }; Server[9] = gone;
            EditBackLater(gone, 1, false); Server.Remove(9);
            Time.realtimeSinceStartup = t + 2; EditBackTick();
            check(Sent.Count == 0 && _editBack.Count == 0, "nothing for an object that went away, and nothing left waiting");

            // what mustn't be asked
            EditBackLater(car, -1, false); EditBackLater(car, 3, true);
            check(_editBack.Count == 0, "no item, or a malformed message: nothing");
            RefusedEditsComeBack = false; EditBackLater(car, 3, false); RefusedEditsComeBack = true;
            check(_editBack.Count == 0, "switched off: nothing (before)");

            // a flood of refusals costs a bounded table
            for (int k = 0; k < 5000; k++) EditBackLater(car, k, false);
            check(_editBack.Count == EditBackMax, "the table stays bounded");

            Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} of {checks} FAILED");
            return fails == 0 ? 0 : 1;
        }
    }
}

public static class EditBackTest { public static int Main() => Entities.Run(); }
