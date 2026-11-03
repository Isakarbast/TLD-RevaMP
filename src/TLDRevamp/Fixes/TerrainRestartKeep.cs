using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Changing the draw distance dropped the player through the ground. terrainGenScript.Upd restarts the generator
    /// whenever the setting differs from the one it was built with (Restart → FStart), and FStart destroys every tile at
    /// once, colliders included, before the first new one exists. The new tiles come one at a time: CheckPlace first
    /// runs up to 9 updates later, then a tile every other update (built on a worker by AsyncTerrain). Standing on
    /// terrain, the player and every loose body around them fell for those frames, and the new tile then appeared above
    /// them — a mesh collider doesn't push anything back up through its surface.
    ///
    /// Same grid (ReuseTiles): the detailed terrain has 100 m tiles of resolution 40 at both ends of the slider — only
    /// its reach changes (300-500 m) — so the restart destroyed ~177 tiles to build the same ~177 again. A restart that
    /// would build the same tile size and resolution now keeps its tiles where they are, in the generator's own
    /// dictionary: nothing is destroyed or rebuilt, and the game's next CheckPlace/CheckRemove grow or shrink the ring.
    ///
    /// Other grid (no generator with colliders today; a mod or a game update could change that): the restart keeps the
    /// tiles it would destroy, colliders and all, under their old parent. An old tile goes once every new tile over its
    /// square is placed, or at once when the new generation doesn't need its square at all. What is left once KeepMaxS
    /// passes with no tile replaced goes too. Only for generators with colliders: the distant terrains, whose tiles grow
    /// with the slider, have none and keep the game's restart.
    ///
    /// Both only when the world is the same: same map, same seed — the draw-distance restart, not a new game.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.TerrainRestartKeep.Enabled / .ReuseTiles
    [HarmonyPatch]
    public static class TerrainRestartKeep
    {
        public static bool Enabled = true;
        public static bool ReuseTiles = true;   // A/B: false = keep-until-replaced for every restart (d667850)
        public static float KeepMaxS = 30f;
        public static long Restarts, Reused, ReusedTiles, Kept, Replaced, Unneeded, TimedOut, Errors;
        public static string LastError = "";

        private sealed class Old { public Vector3d G; public float Size; public newTerrainTileScript Tile; }

        private sealed class Keep
        {
            public readonly List<Old> Tiles = new List<Old>();
            public readonly List<Transform> Parents = new List<Transform>();
            public float Since;
        }

        private static readonly ConditionalWeakTable<terrainGenScript, Keep> Keeps = new ConditionalWeakTable<terrainGenScript, Keep>();
        private static readonly HashSet<long> _needed = new HashSet<long>(), _placed = new HashSet<long>();

        /// The tiles and their parent a restart with an unchanged grid hands back to the generator (FStart → AfterFStart).
        private sealed class Reuse { public Dictionary<Vector3d, newTerrainTileScript> Tiles; public Transform Parent; }

        /// Would the restart build the same tiles? FStart's own sums for the new tile size and resolution; the rest of a
        /// tile's mesh comes from the seed and the roads and buildings, which a restart doesn't change.
        private static bool SameGrid(terrainGenScript gen)
        {
            float lerp = settingsscript.s.S.GetDrawDistanceLerp();
            return Mathf.Round(Mathf.Lerp(gen.minDrawDist.tileSize, gen.maxDrawDist.tileSize, lerp)) == gen.tileSize &&
                   Mathf.RoundToInt(Mathf.Lerp(gen.minDrawDist.resolution, gen.maxDrawDist.resolution, lerp)) == gen.resolution;
        }

        /// Before FStart: what it would destroy is taken out of its reach (it destroys what terrains and parent hold).
        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.FStart))]
        [HarmonyPrefix]
        private static void BeforeFStart(terrainGenScript __instance, mapSettingScript _mapSetting, out Reuse __state)
        {
            __state = null;
            var gen = __instance;
            if (!Enabled || gen.terrains == null || _mapSetting == null || _mapSetting != gen.mapSetting ||
                _mapSetting.seed != gen.seed || _mapSetting.useAsync2 || settingsscript.s == null) return;
            try
            {
                Restarts++;
                if (ReuseTiles && SameGrid(gen))
                {
                    __state = new Reuse { Tiles = gen.terrains, Parent = gen.parent };
                    Reused++; ReusedTiles += gen.terrains.Count;
                    gen.terrains = null;
                    gen.parent = null;
                    return;
                }
                if (!gen.hasCollider) return;
                var keep = Keeps.GetValue(gen, _ => new Keep());
                keep.Since = Time.realtimeSinceStartup;
                if (gen.terrains.Count == 0) return;
                foreach (var kv in gen.terrains)
                    if (kv.Value != null) { keep.Tiles.Add(new Old { G = kv.Key, Size = gen.tileSize, Tile = kv.Value }); Kept++; }
                if (gen.parent != null) keep.Parents.Add(gen.parent);
                gen.terrains = null;
                gen.parent = null;
            }
            catch (Exception e) { Fail(e); }
        }

        /// After FStart: an unchanged grid gets its tiles back, in the same dictionary under the same parent (FStart's new,
        /// empty parent goes). The game's next CheckPlace/CheckRemove then add and drop tiles at the edge of the new reach,
        /// as when the player drives on.
        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.FStart))]
        [HarmonyPostfix]
        private static void AfterFStart(terrainGenScript __instance, Reuse __state)
        {
            if (__state == null) return;
            var gen = __instance;
            try
            {
                if (__state.Parent != null)
                {
                    if (gen.parent != null && gen.parent != __state.Parent) UnityEngine.Object.Destroy(gen.parent.gameObject);
                    gen.parent = __state.Parent;
                }
                gen.terrains = __state.Tiles;
            }
            catch (Exception e) { Fail(e); }
        }

        /// After each update of the generator: old tiles whose ground the new generation has placed (or doesn't want) go.
        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.Upd))]
        [HarmonyPostfix]
        private static void AfterUpd(terrainGenScript __instance)
        {
            if (!Keeps.TryGetValue(__instance, out var keep) || keep.Tiles.Count == 0 && keep.Parents.Count == 0) return;
            try { Sweep(__instance, keep); }
            catch (Exception e) { Fail(e); DropAll(keep); }
        }

        private static void Sweep(terrainGenScript gen, Keep keep)
        {
            if (!Enabled || !gen.isActiveAndEnabled || Time.realtimeSinceStartup - keep.Since > KeepMaxS)
            {
                TimedOut += keep.Tiles.Count;
                DropAll(keep);
                return;
            }
            var needed = gen.coordsNeeded;
            if (needed == null || needed.Count == 0 || gen.terrains == null) return;   // the new generation hasn't asked for its tiles yet
            float ts = gen.tileSize;
            if (!(ts > 0f)) return;
            _needed.Clear(); _placed.Clear();
            foreach (var c in needed) _needed.Add(Cell(c.x, c.z, ts));
            foreach (var kv in gen.terrains) if (kv.Value != null) _placed.Add(Cell(kv.Key.x, kv.Key.z, ts));
            for (int i = keep.Tiles.Count - 1; i >= 0; i--)
            {
                var o = keep.Tiles[i];
                if (o.Tile == null) { keep.Tiles.RemoveAt(i); continue; }
                if (Waiting(o, ts, out bool wanted)) continue;
                o.Tile.Destroy();
                keep.Tiles.RemoveAt(i);
                if (wanted) Replaced++; else Unneeded++;
                keep.Since = Time.realtimeSinceStartup;   // KeepMaxS counts from the last progress: 177 tiles take seconds
            }
            if (keep.Tiles.Count == 0) DropAll(keep);
        }

        /// Does an old tile still hold up ground the new generation wants but hasn't placed? Tiles are squares centred on
        /// their coordinate (GenerateTileMesh starts at −tileSize/2); the new tiles overlapping the old square are checked.
        private static bool Waiting(Old o, float ts, out bool wanted)
        {
            wanted = false;
            const double eps = 0.01;
            double h = o.Size * 0.5;
            long x0 = (long)Math.Floor((o.G.x - h + eps) / ts + 0.5), x1 = (long)Math.Floor((o.G.x + h - eps) / ts + 0.5);
            long z0 = (long)Math.Floor((o.G.z - h + eps) / ts + 0.5), z1 = (long)Math.Floor((o.G.z + h - eps) / ts + 0.5);
            for (long x = x0; x <= x1; x++)
                for (long z = z0; z <= z1; z++)
                {
                    long k = Key(x, z);
                    if (!_needed.Contains(k)) continue;
                    wanted = true;
                    if (!_placed.Contains(k)) return true;
                }
            return false;
        }

        private static long Cell(double x, double z, float ts) => Key((long)Math.Round(x / ts), (long)Math.Round(z / ts));
        private static long Key(long x, long z) => (x << 32) ^ (z & 0xFFFFFFFFL);

        private static void DropAll(Keep keep)
        {
            foreach (var o in keep.Tiles) if (o.Tile != null) o.Tile.Destroy();
            keep.Tiles.Clear();
            foreach (var p in keep.Parents) if (p != null) UnityEngine.Object.Destroy(p.gameObject);
            keep.Parents.Clear();
        }

        private static void Fail(Exception e)
        {
            Errors++;
            LastError = e.GetType().Name + ": " + e.Message;
            if (Errors <= 5) Plugin.Log.LogError("TerrainRestartKeep: " + e);
        }

        /// Bridge `terrainkeep`: counters, what is still kept, and the generators' tile grids at both ends of the slider.
        public static string Stats()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"enabled\":").Append(Enabled ? "true" : "false").Append(",\"reuse\":").Append(ReuseTiles ? "true" : "false")
              .Append(",\"restarts\":").Append(Restarts).Append(",\"reused\":").Append(Reused).Append(",\"reusedTiles\":").Append(ReusedTiles).Append(",\"kept\":").Append(Kept)
              .Append(",\"replaced\":").Append(Replaced).Append(",\"unneeded\":").Append(Unneeded).Append(",\"timedOut\":").Append(TimedOut)
              .Append(",\"errors\":").Append(Errors).Append(",\"lastError\":").Append(Json.Str(LastError)).Append(",\"generators\":[");
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.terrainGen != null)
            {
                bool first = true;
                foreach (var g in map.terrainGen)
                {
                    if (g == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    int waiting = Keeps.TryGetValue(g, out var k) ? k.Tiles.Count : 0;
                    sb.Append("{\"name\":").Append(Json.Str(g.terrainName)).Append(",\"collider\":").Append(g.hasCollider ? "true" : "false")
                      .Append(",\"tileSize\":").Append(Json.Num(g.tileSize)).Append(",\"resolution\":").Append(g.resolution)
                      .Append(",\"tiles\":").Append(g.terrains != null ? g.terrains.Count : 0).Append(",\"keptWaiting\":").Append(waiting)
                      .Append(",\"min\":{\"draw\":").Append(Json.Num(g.minDrawDist.drawDistance)).Append(",\"tile\":").Append(Json.Num(g.minDrawDist.tileSize)).Append(",\"res\":").Append(g.minDrawDist.resolution)
                      .Append("},\"max\":{\"draw\":").Append(Json.Num(g.maxDrawDist.drawDistance)).Append(",\"tile\":").Append(Json.Num(g.maxDrawDist.tileSize)).Append(",\"res\":").Append(g.maxDrawDist.resolution).Append("}}");
                }
            }
            return sb.Append("]}").ToString();
        }
    }
}
