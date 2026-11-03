using System.Collections.Generic;

namespace TLDRevamp.Fixes
{
    /// Game bug (every terrain, poiGenScript.AlignPosToRoad): a building goes to the road nearest its candidate spot, and
    /// NearestPointOnRoadBone keeps the first of two roads at the same distance. Ties are common: when the nearest point
    /// is a junction or a road's end, every road meeting there is equally near. The list comes from RoadsNear in the order
    /// the roads were added to their chunk, and a chunk's links to its neighbours are added when the game gets to them, so
    /// on one machine a link came before the chunk's own roads and on another after. Measured (seed 31337, two copies
    /// arriving from north and south, 2026-11-03): two roads 6132.4 m from a main road building's candidate, meeting at
    /// (0, 234123); one copy put the building 2 km down one road, the other down the other.
    ///
    /// Fix: the candidates in an order that doesn't depend on that: by the chunk that holds them (the game's own loop
    /// order, south to north, west to east), the chunk's own roads first in their own order (as the game has them when
    /// its links come later, the usual case), then its links by their ends. Used for the list AlignPosToRoad picks from
    /// (AlignNearestRoad.Candidates_). Runtime switch (bridge `set`): TLDRevamp.Fixes.RoadTieOrder.Enabled.
    public static class RoadTieOrder
    {
        public static bool Enabled = true;
        public static long Sorts, Moved;

        private static int Compare(roadGenScript.roadClass a, roadGenScript.roadClass b)
        {
            if (ReferenceEquals(a, b)) return 0;
            int c = a.selfChunk.z.CompareTo(b.selfChunk.z); if (c != 0) return c;
            c = a.selfChunk.x.CompareTo(b.selfChunk.x); if (c != 0) return c;
            bool la = !a.chunk1.Equals(a.chunk2), lb = !b.chunk1.Equals(b.chunk2);
            if (la != lb) return la ? 1 : -1;
            if (!la) return a.selfID.CompareTo(b.selfID);   // the chunk's own roads: always made together, in one order
            c = a.start.x.CompareTo(b.start.x); if (c != 0) return c;
            c = a.start.z.CompareTo(b.start.z); if (c != 0) return c;
            c = a.end.x.CompareTo(b.end.x); if (c != 0) return c;
            return a.end.z.CompareTo(b.end.z);
        }

        /// In place, stable for equal keys (the same road twice from the road index stays adjacent).
        public static void Sort(List<roadGenScript.roadClass> list)
        {
            if (!Enabled || list == null || list.Count < 2) return;
            Sorts++;
            var before = list.ToArray();
            // insertion sort: stable, and the lists are short (tens)
            for (int i = 1; i < list.Count; i++)
            {
                var x = list[i]; int j = i - 1;
                while (j >= 0 && Compare(list[j], x) > 0) { list[j + 1] = list[j]; j--; }
                list[j + 1] = x;
            }
            for (int i = 0; i < before.Length; i++) if (!ReferenceEquals(before[i], list[i])) { Moved++; break; }
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"sorts\":" + Sorts + ",\"moved\":" + Moved + "}";
    }
}
