using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Game bug (crossroads worlds, roadGenScript.ConnectChunks): the roads between two neighbouring 10 km road chunks
    /// depend on which of the two the game handles first, so on the direction the area was reached from.
    ///
    /// ConnectDots(c1, c2, ..., plussz) joins a dot of one chunk to a dot of the other unless another dot lies inside the
    /// circle over the pair; the dots of the two chunks count, and those of ONE third chunk, `plussz`. ConnectChunks passes
    /// a different third chunk depending on which of the two it is working on when it makes the link:
    ///  - lower and upper (same x): from the lower one the chunk right of the lower; from the upper one the chunk left of
    ///    the upper;
    ///  - left and right (same z): from the left one the chunk above the left; from the right one the chunk below the right.
    /// It works on the needed chunks nearest the player first, so a player arriving from the north gets other links (and
    /// with them other roads, buildings along them and scenery) than one arriving from the south: two players, or one
    /// player before and after loading a save far from where the area was first made.
    ///
    /// Fix: the third chunk is always the one the game uses when it works outward from the world's start (the chunk
    /// nearer the start makes the link): the world a player gets who drove out from the start, which is the usual way an
    /// area is reached, now on every machine and whatever the route. Both third chunks are inside the 3x3 the game keeps
    /// around any chunk it links; one that isn't there yet is made by the game's own UpdDotChunk (its dots come from the
    /// seed). Main road only worlds link through WorldGen.MainRoadOnlyRoads and are left alone.
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.ChunkLinks.Enabled.
    [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.ConnectDots))]
    public static class ChunkLinks
    {
        public static bool Enabled = true;
        public static long Links, Changed, Made;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]   // before RoadConnectMemo, which remembers the call by its inputs
        private static void Prefix(roadGenScript __instance, Vector3d _c1, Vector3d _c2, ref List<roadGenScript.dotClass> plussz)
        {
            if (!Enabled || _c1.Equals(_c2) || !__instance.hasSideRoad) return;
            double size = __instance.chunkSize;
            long dx = (long)System.Math.Round((_c1.x - _c2.x) / size), dz = (long)System.Math.Round((_c1.z - _c2.z) / size);
            Vector3d third;
            if (dx == 0 && dz == 1)
            {
                // c1 above c2: the nearer one to the start makes the link (|z| smaller)
                third = System.Math.Abs(_c2.z) <= System.Math.Abs(_c1.z)
                    ? mainscript.GetChunkPos(_c2, 1, 0, __instance.chunkSize)      // from the lower: right of the lower
                    : mainscript.GetChunkPos(_c1, -1, 0, __instance.chunkSize);    // from the upper: left of the upper
            }
            else if (dx == 1 && dz == 0)
            {
                // c1 right of c2
                third = System.Math.Abs(_c2.x) <= System.Math.Abs(_c1.x)
                    ? mainscript.GetChunkPos(_c2, 0, 1, __instance.chunkSize)      // from the left: above the left
                    : mainscript.GetChunkPos(_c1, 0, -1, __instance.chunkSize);    // from the right: below the right
            }
            else return;
            Links++;
            if (!__instance.chunks.TryGetValue(third, out var chunk))
            {
                __instance.UpdDotChunk(third);
                if (!__instance.chunks.TryGetValue(third, out chunk)) return;
                Made++;
            }
            if (!ReferenceEquals(chunk.dots, plussz)) Changed++;
            plussz = chunk.dots;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"links\":" + Links + ",\"changed\":" + Changed + ",\"made\":" + Made + "}";
    }
}
