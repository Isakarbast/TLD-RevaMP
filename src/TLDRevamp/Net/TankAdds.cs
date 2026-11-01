using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    /// What a player poured into or out of a tank on a copy, as a change and not a level: Entities.Edits. Two players at
    /// one barrel each had the level as their own machine saw it, the owner took the last one, and the other's pour was
    /// gone from the barrel while their can stayed empty.
    public static class TankAdds
    {
        public struct Add { public int Tank, Type; public float Litres; }
        public const int MaxAdds = 64;
        private const float Smallest = 1e-4f;

        /// Tank `tank` from `from` to `now`, a row per fluid that changed. A tank with no record is empty.
        public static void Diff(int tank, int[] fromTypes, float[] fromAmounts, int[] nowTypes, float[] nowAmounts, List<Add> into)
        {
            int nf = fromTypes != null && fromAmounts != null ? Math.Min(fromTypes.Length, fromAmounts.Length) : 0;
            int nn = nowTypes != null && nowAmounts != null ? Math.Min(nowTypes.Length, nowAmounts.Length) : 0;
            for (int i = 0; i < nn; i++)
            {
                if (SeenBefore(nowTypes, i)) continue;
                float d = Sum(nowTypes, nowAmounts, nn, nowTypes[i]) - Sum(fromTypes, fromAmounts, nf, nowTypes[i]);
                if (Math.Abs(d) >= Smallest) into.Add(new Add { Tank = tank, Type = nowTypes[i], Litres = d });
            }
            for (int i = 0; i < nf; i++)
            {
                if (SeenBefore(fromTypes, i) || Has(nowTypes, nn, fromTypes[i])) continue;
                float d = Sum(fromTypes, fromAmounts, nf, fromTypes[i]);
                if (d >= Smallest) into.Add(new Add { Tank = tank, Type = fromTypes[i], Litres = -d });
            }
        }

        /// What of a change fits: no more than the room left, no less than what the tank holds of that fluid.
        public static float Allowed(float litres, float have, float room) => litres >= 0f ? Math.Min(litres, Math.Max(room, 0f)) : Math.Max(litres, -Math.Max(have, 0f));

        public static void Write(NetWriter w, List<Add> adds)
        {
            w.VarU32((uint)adds.Count);
            foreach (var a in adds) { w.VarU32((uint)a.Tank); w.VarU32((uint)a.Type); w.F32(a.Litres); }
        }

        /// False when it is malformed: nothing of it counts.
        public static bool Read(NetReader r, List<Add> into)
        {
            into.Clear();
            uint n = r.VarU32();
            if (r.Bad || n > MaxAdds) return false;
            for (uint i = 0; i < n; i++)
            {
                uint tank = r.VarU32(), type = r.VarU32(); float l = r.F32();
                if (r.Bad || tank > 4096 || type > 4096 || float.IsNaN(l) || float.IsInfinity(l)) { into.Clear(); return false; }
                into.Add(new Add { Tank = (int)tank, Type = (int)type, Litres = l });
            }
            return true;
        }

        private static bool SeenBefore(int[] types, int i) { for (int k = 0; k < i; k++) if (types[k] == types[i]) return true; return false; }
        private static bool Has(int[] types, int n, int type) { for (int k = 0; k < n; k++) if (types[k] == type) return true; return false; }
        private static float Sum(int[] types, float[] amounts, int n, int type) { float s = 0f; for (int k = 0; k < n; k++) if (types[k] == type) s += amounts[k]; return s; }
    }
}
