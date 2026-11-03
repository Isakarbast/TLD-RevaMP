using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Buildings find their road from the seed alone (crossroads and main road only worlds).
    ///
    /// The game places a building at the road nearest its candidate spot, out of the roads its road generator holds at
    /// that moment (poiGenScript.AlignPosToRoad -> roadGenScript.RoadsNear; the road-aware height of the others,
    /// mapSettingScript.RoadsNear). What it holds depends on where the player has been and in which order, so two
    /// players (or one before and after a reload) could get a building in other places. PoiRoadsSettled made buildings
    /// wait until every road around them was finished; this answers the question from the seed instead, the way the
    /// world's terrain is: the same answer at any moment, on any machine, whatever was loaded before.
    ///
    /// The roads themselves already are a function of the seed: a road chunk's dots come from its own Random (seed and
    /// chunk position) and terrain height, the roads inside it and its links to the chunks above and right from those
    /// dots (the links' third chunk fixed by ChunkLinks, main road only links by MainRoadOnlyRoads), a road's shape from
    /// its two dots, noise and terrain height. So each road generator gets a shadow: a second roadGenScript with the
    /// same settings that is never drawn and never placed, driven through the game's own UpdChunks / ConnectChunks /
    /// roadClass.Generate (and every fix on them) for exactly the chunks a lookup needs. A chunk counts as finished there
    /// once its dots are joined and both its links are made: the state the live generator reaches when the player has
    /// been around it. RoadsNear's three steps then run on the shadow, as the game has them, with only the roads that can
    /// still be nearest generated (AlignNearestRoad's bound):
    ///  1. the roads registered in the 3x3 inner chunks around the spot, of the 3x3 road chunks;
    ///  2. none: every road of those 3x3 road chunks;
    ///  3. none: the game takes every road it has loaded (depends on what is loaded); here: rings of road chunks outward
    ///     until no farther ring can hold a nearer road.
    /// Ties go to RoadTieOrder's order. With the answer pure, buildings no longer wait for roads (RoadsDoneAtPlace is
    /// true inside AddPois, up to BudgetMs of lookups per frame: the rest go next frame, which changes when they appear,
    /// never where).
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.PureRoads.Enabled (buildings use the shadow), .Compare (with
    /// Enabled off: every placement the live generator makes is also looked up in the shadow and compared), .BudgetMs,
    /// .EvictChunks. Diagnostics: bridge `call TLDRevamp.Fixes.PureRoads.Stats` / `.Settings`.
    [HarmonyPatch]
    public static class PureRoads
    {
        /// On by default since it was measured equal to the waiting rule: 4,778 placements compared, 0 different (exact).
        public static bool Enabled = true;
        public static bool Compare = false;
        /// Measured (one copy alone, CPUs 0-15, 60 FPS, ~1,800 km/h, 3+3 rounds on/off): 2 ms keeps AddPois under 3.8 ms per
        /// frame, as the waiting rule (4 ms: frames up to 8.9 ms, a road generated on top of the budget).
        public static float BudgetMs = 2f;
        /// Shadow chunks farther than this many road chunks from every player are dropped (they are made again from the
        /// seed when needed).
        public static int EvictChunks = 4;
        public static int MaxRing = 6;

        public static long Lookups, Step1, Step2, Step3, NotFound, Generated, Pruned, ChunksFinished, Unfinished, Resets, Evicted,
            HeldForBudget, Compared, Differ, Errors;
        public static double LookupMs, MaxLookupMs, WorstDifferM;
        public static string LastDiffer = "", LastError = "";

        private sealed class Shadow
        {
            public roadGenScript Real, G;
            public string Key;
            public readonly HashSet<Vector3d> Finished = new HashSet<Vector3d>();
            public float EvictAt;
        }

        private static readonly ConditionalWeakTable<roadGenScript, Shadow> Shadows = new ConditionalWeakTable<roadGenScript, Shadow>();
        private static readonly ConditionalWeakTable<roadGenScript, roadGenScript> IsShadowOf = new ConditionalWeakTable<roadGenScript, roadGenScript>();
        private static readonly List<Shadow> All = new List<Shadow>();

        private static readonly AccessTools.FieldRef<roadGenScript, int> Baround = AccessTools.FieldRefAccess<roadGenScript, int>("baround");
        private static readonly AccessTools.FieldRef<roadGenScript, int> Iaround = AccessTools.FieldRefAccess<roadGenScript, int>("iaround");

        /// A shadow generator: the fixes that react to roads being made for the world (scenery, telemetry) skip it.
        internal static bool IsShadow(roadGenScript g) => g != null && IsShadowOf.TryGetValue(g, out _);

        /// The live generator a shadow stands for (null for any other): fixes that act on particular generators (the
        /// old road) act on its shadow too.
        internal static roadGenScript RealOf(roadGenScript g) => g != null && IsShadowOf.TryGetValue(g, out var real) ? real : null;

        /// A shadow can stand for this generator (every terrain mode: the old road is made by its own fixes on the shadow too).
        internal static bool ShadowUsable(roadGenScript g) =>
            g != null && !IsShadow(g) && (g.hasSideRoad || g.hasMainRoad) && g.mapSetting != null && g.terrainSettings != null
            && g.mapSetting.imageMapLoad == null;

        /// Buildings from the shadow: not in old terrain, whose buildings are the 2023 plan (WorldGen.Old2023.OldBuildings).
        internal static bool Usable(roadGenScript g) => ShadowUsable(g) && !WorldGen.Old2023.OldWorld.Active;

        internal static bool Applies(roadGenScript g) => Enabled && Usable(g);

        // ------------------------------------------------------------------ the shadow

        /// Everything the shadow's roads depend on besides the seed's chunks: a change makes a new shadow.
        private static string KeyOf(roadGenScript g)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join("|", g.mapSetting.seed.ToString(ic), g.hasMainRoad ? "M" : "m", g.hasSideRoad ? "S" : "s",
                g.chunkSize.ToString("R", ic), g.innerChunkSize.ToString("R", ic), g.minDotCount.ToString(ic), g.maxDotCount.ToString(ic),
                g.emptySideRoadChance.ToString("R", ic), g.marginPercent.ToString("R", ic), g.minDistPercent.ToString("R", ic),
                g.maxTries.ToString(ic), g.offsetty.ToString("R", ic), g.temporaryHeightSmoothing ? "T" : "t",
                g.temporaryMaxHeightDifference.ToString("R", ic), g.terrainSettings.GetInstanceID().ToString(ic),
                g.mapSetting.GetInstanceID().ToString(ic), ChunkLinks.Enabled ? "L" : "l", WorldGen.MainRoadOnlyRoads.Enabled ? "O" : "o",
                WorldGen.Old2023.OldWorld.Active ? "2023" : "beta");
        }

        private static Shadow Get(roadGenScript real)
        {
            string key = KeyOf(real);
            if (Shadows.TryGetValue(real, out var s))
            {
                if (s.G != null && s.Key == key) return s;
                Drop(s);
                Shadows.Remove(real);
                Resets++;
            }
            for (int i = All.Count - 1; i >= 0; i--) if (All[i].Real == null) { Drop(All[i]); All.RemoveAt(i); }   // its world is gone
            var go = new GameObject("RevaMP pure roads");
            var g = go.AddComponent<roadGenScript>();
            // the generator's settings (numbers, flags, structs, the terrain and map it reads); never its chunks, lists or
            // the transform its road pieces go under (the shadow places nothing)
            foreach (var f in AccessTools.GetDeclaredFields(typeof(roadGenScript)))
            {
                if (f.IsStatic || f.IsInitOnly) continue;
                var t = f.FieldType;
                bool unityRef = typeof(UnityEngine.Object).IsAssignableFrom(t);
                if (!(t.IsValueType || t == typeof(string) || unityRef)) continue;
                if (f.Name == nameof(roadGenScript.parent) || f.Name == "ropeRope") continue;
                f.SetValue(g, f.GetValue(real));
            }
            g.chunks = new Dictionary<Vector3d, roadGenScript.dotChunkClass>();
            g.chunksNeeded = new List<Vector3d>();
            g.chunksToRemove = new List<Vector3d>();
            g.parent = null;
            IsShadowOf.Add(g, real);
            s = new Shadow { Real = real, G = g, Key = key };
            Shadows.Add(real, s);
            All.Add(s);
            return s;
        }

        private static void Drop(Shadow s)
        {
            if (s.G != null) UnityEngine.Object.Destroy(s.G.gameObject);
            s.G = null;
            s.Finished.Clear();
        }

        private static readonly List<Vector3d> Todo = new List<Vector3d>();

        /// Every road chunk of the (2ring+1)^2 square around p's chunk finished in the shadow: dots joined into roads, links
        /// to the chunks above and right made (main road only: the link up decided).
        private static void Finish(Shadow s, Vector3d p, int ring)
        {
            var g = s.G;
            var list = new List<Vector3d>();
            for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                    list.Add(mainscript.GetChunkPos(p, dx, dz, g.chunkSize));
            Finish(s, list);
        }

        private static void Finish(Shadow s, List<Vector3d> chunks)
        {
            var g = s.G;
            bool mainOnly = !g.hasSideRoad;
            Todo.Clear();
            foreach (var c in chunks)
            {
                if (mainOnly && c.x != 0.0) continue;   // the game makes no chunk off the main road's line
                if (!s.Finished.Contains(c) && !Todo.Contains(c)) Todo.Add(c);
            }
            if (Todo.Count == 0) return;
            var need = g.chunksNeeded;
            need.Clear();
            foreach (var c in Todo)
            {
                if (!need.Contains(c)) need.Add(c);
                // main road only links a chunk to the one above when both are needed (MainRoadOnlyRoads.AfterConnectChunks)
                if (mainOnly) { var up = mainscript.GetChunkPos(c, 0, 1, g.chunkSize); if (!need.Contains(up)) need.Add(up); }
            }
            g.chunksToRemove.Clear();
            g.UpdChunks(false);     // dots of each needed chunk and its 3x3, its own roads
            g.ConnectChunks();      // links (and main road only's, in its postfix)
            need.Clear();
            foreach (var c in Todo)
            {
                if (IsFinished(g, c)) { s.Finished.Add(c); ChunksFinished++; }
                else Unfinished++;
            }
        }

        private static bool IsFinished(roadGenScript g, Vector3d c)
        {
            if (!g.chunks.TryGetValue(c, out var ch) || !ch.insideConnected) return false;
            if (!g.hasSideRoad)
                return g.chunks.TryGetValue(mainscript.GetChunkPos(c, 0, 1, g.chunkSize), out var up) && up.insideConnected;
            return ch.outsideUConnected && ch.outsideRConnected;
        }

        /// Drop the shadow chunks far from every player; the chunks next to them aren't finished any more (a link of
        /// theirs may lead into a dropped one: made again with it when asked).
        private static void Evict(Shadow s)
        {
            float now = Time.realtimeSinceStartup;
            if (now < s.EvictAt) return;
            s.EvictAt = now + 2f;
            var g = s.G;
            var players = new List<Vector3d>();
            foreach (var ga in g.mapSetting.genArounds) players.Add(mainscript.GetChunkPos(mainscript.GlobalFromUnityPos(ga.upos), g.chunkSize));
            if (players.Count == 0) return;
            List<Vector3d> drop = null;
            foreach (var c in g.chunks.Keys)
            {
                bool near = false;
                foreach (var pc in players)
                    if (Math.Max(Math.Abs(c.x - pc.x), Math.Abs(c.z - pc.z)) <= EvictChunks * (double)g.chunkSize + 1.0) { near = true; break; }
                if (!near) (drop ?? (drop = new List<Vector3d>())).Add(c);
            }
            if (drop == null) return;
            foreach (var c in drop)
            {
                g.chunks.Remove(c);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        s.Finished.Remove(mainscript.GetChunkPos(c, dx, dz, g.chunkSize));
                Evicted++;
            }
        }

        // ------------------------------------------------------------------ the lookup

        private struct Cand { public roadGenScript.roadClass R; public int Index; public double Lb; }

        /// RoadsNear(pos, notJustChunks, align) as the game answers it once every road chunk around pos is finished,
        /// generated only as far as the nearest road needs: the roads AlignPosToRoad / the road-aware height pick from
        /// (all generated, in RoadTieOrder's order).
        internal static List<roadGenScript.roadClass> Near(roadGenScript real, Vector3d pos, bool notJustChunks, poiGenScript.alignToRoadEmu align)
        {
            var sw = Stopwatch.StartNew();
            Lookups++;
            var s = Get(real);
            var g = s.G;
            Evict(s);
            int b = Baround(g);
            Finish(s, pos, b);
            double lateral = AlignNearestRoad.MaxLateral(g);

            // step 1: roads with a piece starting in the inner chunks around pos (the game's road index)
            var keys = new List<Vector3d>();
            int ia = Iaround(g);
            for (int k = -ia; k <= ia; k++)
                for (int l = -ia; l <= ia; l++)
                    keys.Add(mainscript.GetChunkPos(pos, l, k, g.innerChunkSize));
            var center = mainscript.GetChunkPos(pos, g.innerChunkSize);
            double half = (ia + 0.5) * g.innerChunkSize + 1.0;
            double minX = center.x - half, maxX = center.x + half, minZ = center.z - half, maxZ = center.z + half;

            var cands = new List<Cand>();
            for (int i = -b; i <= b; i++)
                for (int j = -b; j <= b; j++)
                    Collect(g, mainscript.GetChunkPos(pos, j, i, g.chunkSize), align, pos, lateral, cands);

            var result = new List<roadGenScript.roadClass>();
            double best = double.MaxValue;
            var step1 = new List<Cand>();
            foreach (var c in cands)
                if (RectDist(c.R.start, c.R.end, minX, maxX, minZ, maxZ) <= lateral) step1.Add(c);
            Nearest(g, pos, step1, ref best, result, r => Registered(g, r, keys));
            if (_notReady) return null;
            if (result.Count > 0 || !notJustChunks)
            {
                if (result.Count > 0) Step1++;
                return Done(sw, result);
            }

            // step 2: every road of the 3x3 road chunks
            Nearest(g, pos, cands, ref best, result, null);
            if (_notReady) return null;
            if (result.Count > 0) { Step2++; return Done(sw, result); }

            // step 3: rings outward, until no farther ring can hold a nearer road (a chunk's roads reach into the chunks
            // above and right of it, and lateral metres off their straight line)
            for (int ring = b + 1; ring <= MaxRing; ring++)
            {
                if (result.Count > 0 && (ring - 2) * (double)g.chunkSize - lateral > best) break;
                Finish(s, pos, ring);
                var ringCands = new List<Cand>();
                for (int i = -ring; i <= ring; i++)
                    for (int j = -ring; j <= ring; j++)
                        if (Math.Max(Math.Abs(i), Math.Abs(j)) == ring)
                            Collect(g, mainscript.GetChunkPos(pos, j, i, g.chunkSize), align, pos, lateral, ringCands);
                Nearest(g, pos, ringCands, ref best, result, null);
                if (_notReady) return null;
            }
            if (result.Count > 0) Step3++; else NotFound++;
            return Done(sw, result);
        }

        /// Every road that can come within `reach` of the rectangle, generated, in RoadTieOrder's order: the roads a
        /// distant terrain tile is pulled onto (TerrainHorizon). A chunk's roads stay inside it and the chunks above and
        /// right of it, `lateral` metres off their straight lines. Null when a road still has to be generated and the
        /// budget (`budgetMs` on `budget`) is spent: ask again next frame, the roads made so far stay.
        internal static List<roadGenScript.roadClass> RoadsAround(roadGenScript real, double minX, double maxX, double minZ, double maxZ, double reach,
            Stopwatch budget, double budgetMs)
        {
            if (!ShadowUsable(real)) return null;
            var s = Get(real);
            var g = s.G;
            Evict(s);
            double lateral = AlignNearestRoad.MaxLateral(g), size = g.chunkSize;
            minX -= reach; maxX += reach; minZ -= reach; maxZ += reach;
            var center = mainscript.GetChunkPos(new Vector3d((minX + maxX) * 0.5, 0.0, (minZ + maxZ) * 0.5), g.chunkSize);
            var chunks = new List<Vector3d>();
            for (int dz = -2; dz <= 1; dz++)
                for (int dx = -2; dx <= 1; dx++)
                {
                    var c = mainscript.GetChunkPos(center, dx, dz, g.chunkSize);
                    if (c.x - 0.5 * size - lateral > maxX || c.x + 1.5 * size + lateral < minX || c.z - 0.5 * size - lateral > maxZ || c.z + 1.5 * size + lateral < minZ) continue;
                    chunks.Add(c);
                }
            Finish(s, chunks);
            var result = new List<roadGenScript.roadClass>();
            foreach (var c in chunks)
            {
                if (!g.chunks.TryGetValue(c, out var ch)) continue;
                foreach (var r in ch.roads)
                {
                    if (r == null || RectDist(r.start, r.end, minX, maxX, minZ, maxZ) > lateral) continue;
                    if (!r.generated)
                    {
                        if (budget.Elapsed.TotalMilliseconds >= budgetMs) return null;
                        r.Generate(g);
                        Generated++;
                    }
                    if (r.generated && r.roadElements.Count > 0) result.Add(r);
                }
            }
            RoadTieOrder.Sort(result);
            return result;
        }

        private static List<roadGenScript.roadClass> Done(Stopwatch sw, List<roadGenScript.roadClass> result)
        {
            RoadTieOrder.Sort(result);
            double ms = sw.Elapsed.TotalMilliseconds;
            LookupMs += ms;
            if (ms > MaxLookupMs) MaxLookupMs = ms;
            return result;
        }

        private static void Collect(roadGenScript g, Vector3d chunk, poiGenScript.alignToRoadEmu align, Vector3d pos, double lateral, List<Cand> into)
        {
            if (!g.chunks.TryGetValue(chunk, out var ch)) return;
            for (int n = 0; n < ch.roads.Count; n++)
            {
                var r = ch.roads[n];
                if (r == null || (g.hasMainRoad && !g.CheckAlignRoad(r.roadType, align))) continue;
                into.Add(new Cand { R = r, Index = n, Lb = AlignNearestRoad.SegmentDist(pos, r.start, r.end) - lateral });
            }
        }

        /// The road's pieces as the game's road index has them: a piece starting in one of `keys` (the inner chunks around
        /// the spot) of the road's own chunk.
        private static bool Registered(roadGenScript g, roadGenScript.roadClass r, List<Vector3d> keys)
        {
            if (!g.chunks.TryGetValue(r.selfChunk, out var ch)) return false;
            int id = ch.roads.IndexOf(r);
            if (id < 0) return false;
            foreach (var k in keys)
                if (ch.innerChunks.TryGetValue(k, out var ic) && ic.roads != null && ic.ContainsID(id)) return true;
            return false;
        }

        /// The nearest of `cands` (only those `member` accepts) joins `result` with every road as near: generated most
        /// promising first, a road whose bound is farther than the best found is never generated (it can't be chosen).
        private static void Nearest(roadGenScript g, Vector3d pos, List<Cand> cands, ref double best, List<roadGenScript.roadClass> result,
            Func<roadGenScript.roadClass, bool> member)
        {
            cands.Sort((x, y) => x.Lb.CompareTo(y.Lb));
            foreach (var c in cands)
            {
                if (c.Lb > best) { Pruned++; continue; }
                var r = c.R;
                if (!r.generated)
                {
                    // preparing a building (RoadsReady): roads are generated only while the frame's budget lasts
                    if (_limit && Budget.Elapsed.TotalMilliseconds >= BudgetMs) { _notReady = true; return; }
                    r.Generate(g);
                    Generated++;
                }
                if (!r.generated || r.roadElements.Count == 0) continue;
                if (member != null && !member(r)) continue;
                double d = AlignNearestRoad.RoadDist(pos, r);
                if (d < best)
                {
                    best = d;
                    // farther roads already in the list can't be chosen any more
                    for (int i = result.Count - 1; i >= 0; i--) if (AlignNearestRoad.RoadDist(pos, result[i]) > best) result.RemoveAt(i);
                }
                if (d <= best && !result.Contains(r)) result.Add(r);
            }
        }

        /// Horizontal distance from the segment a-b to the rectangle (0 when it touches it).
        private static double RectDist(Vector3d a, Vector3d b, double minX, double maxX, double minZ, double maxZ)
        {
            if (Inside(a.x, a.z, minX, maxX, minZ, maxZ) || Inside(b.x, b.z, minX, maxX, minZ, maxZ)) return 0;
            if (Crosses(a, b, minX, minZ, maxX, minZ) || Crosses(a, b, maxX, minZ, maxX, maxZ) || Crosses(a, b, maxX, maxZ, minX, maxZ) || Crosses(a, b, minX, maxZ, minX, minZ)) return 0;
            double d = double.MaxValue;
            foreach (var (x, z) in new[] { (minX, minZ), (maxX, minZ), (maxX, maxZ), (minX, maxZ) })
                d = Math.Min(d, AlignNearestRoad.SegmentDist(new Vector3d(x, 0.0, z), a, b));
            d = Math.Min(d, PointRect(a.x, a.z, minX, maxX, minZ, maxZ));
            d = Math.Min(d, PointRect(b.x, b.z, minX, maxX, minZ, maxZ));
            return d;
        }

        private static bool Inside(double x, double z, double minX, double maxX, double minZ, double maxZ) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

        private static double PointRect(double x, double z, double minX, double maxX, double minZ, double maxZ)
        {
            double dx = Math.Max(0, Math.Max(minX - x, x - maxX)), dz = Math.Max(0, Math.Max(minZ - z, z - maxZ));
            return Math.Sqrt(dx * dx + dz * dz);
        }

        private static bool Crosses(Vector3d a, Vector3d b, double cx, double cz, double dx, double dz)
        {
            double d1 = Cross(cx, cz, dx, dz, a.x, a.z), d2 = Cross(cx, cz, dx, dz, b.x, b.z);
            double d3 = Cross(a.x, a.z, b.x, b.z, cx, cz), d4 = Cross(a.x, a.z, b.x, b.z, dx, dz);
            return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0);
        }

        private static double Cross(double ax, double az, double bx, double bz, double px, double pz) => (bx - ax) * (pz - az) - (bz - az) * (px - ax);

        // ------------------------------------------------------------------ the game's calls

        [ThreadStatic] private static int _inAddPois;
        private static readonly Stopwatch Budget = new Stopwatch();
        private static poiGenScript _pg;
        private static bool _limit, _notReady;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyPrefix]
        private static void BeforeAddPois(poiGenScript __instance) { _inAddPois++; _pg = __instance; Budget.Restart(); }

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyFinalizer]
        private static void AfterAddPois() => _inAddPois--;

        /// Buildings don't wait for roads: their road comes from the shadow. Only the frame budget holds them: the lookups
        /// the building will make (its road, or the roads its height blends to) are run here first, generating shadow roads
        /// only while the budget lasts; one that isn't finished goes on next frame with the roads generated so far.
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.RoadsDoneAtPlace))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool RoadsReady(roadGenScript __instance, Vector3d _gpos, ref bool __result)
        {
            if (_inAddPois == 0 || !Applies(__instance)) return true;
            __result = Budget.Elapsed.TotalMilliseconds < BudgetMs && Prepare(__instance, _gpos);
            if (!__result) HeldForBudget++;
            return false;
        }

        private static bool Prepare(roadGenScript g, Vector3d gpos)
        {
            var t = Candidate(gpos);
            _limit = true;
            _notReady = false;
            try
            {
                if (t != null && t.roadType != poiGenScript.alignToRoadEmu.none && _pg != null && _pg.roadAffects)
                    return Near(g, gpos, true, t.roadType) != null;
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map != null && map.roadGens != null)
                    foreach (var rg in map.roadGens)
                        if (Applies(rg) && Near(rg, gpos, false, poiGenScript.alignToRoadEmu.anyRoad) == null) return false;
                return true;
            }
            catch (Exception e) { Fail("prepare", e); return true; }
            finally { _limit = false; _notReady = false; }
        }

        /// The candidate AddPois is asking about (its spot is the one RoadsDoneAtPlace is given).
        private static poiGenScript.tempPoiClass Candidate(Vector3d gpos)
        {
            if (_pg == null) return null;
            foreach (var key in _pg.chunksNeeded)
                if (_pg.chunks.TryGetValue(key, out var ch) && ch.poiToPlace != null)
                    foreach (var t in ch.poiToPlace)
                        if (t != null && t.pos.Equals(gpos)) return t;
            return null;
        }

        /// Two candidates can land on one spot (PoiSpotOrder: the smaller candidate wins and replaces the other, unless that
        /// one is spawned already). With no waiting, a building in sight could be spawned before a candidate that beats it
        /// for its spot is looked up (the frame budget). So a building from a candidate is spawned only once no candidate
        /// in the building chunks within 2 of it (at least 20 km around the spot; a building lands far less than that from
        /// its candidate) is still waiting in a needed chunk: lookups, which finish within a frame or two.
        public static bool HoldSpawns = true;
        public static long SpawnsHeld;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.Spawn))]
        [HarmonyPrefix]
        private static bool BeforeSpawn(poiGenScript __instance, poiGenScript.poiClass _poi, ref GameObject __result)
        {
            if (!HoldSpawns || _poi == null || _poi.pobj != null || !PoiSpotOrder.Enabled || !Applies(__instance.roadgen)
                || !PoiSpotOrder.FromCandidate(_poi) || !PendingNear(__instance, _poi.pos)) return true;
            SpawnsHeld++;
            __result = null;
            return false;
        }

        private static bool PendingNear(poiGenScript pg, Vector3d pos)
        {
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    var k = mainscript.GetChunkPos(pos, dx, dz, pg.chunkSize);
                    if (pg.chunksNeeded.Contains(k) && pg.chunks.TryGetValue(k, out var ch) && ch.poiToPlace != null && ch.poiToPlace.Count > 0) return true;
                }
            return false;
        }

        /// Inside AddPois, mapSettingScript.RoadsNear(pos) (the roads the height of a building not on a road is blended
        /// to): from the shadows.
        public static int HeightPatched;

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AddPois))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> HeightRoadsPatch(IEnumerable<CodeInstruction> instructions)
        {
            var p = new GamePatch(nameof(PureRoads), instructions);
            p.Calls(AccessTools.Method(typeof(mapSettingScript), nameof(mapSettingScript.RoadsNear), new[] { typeof(Vector3d) }),
                AccessTools.Method(typeof(PureRoads), nameof(HeightRoads)), 1);
            return p.Result(ref HeightPatched);
        }

        public static List<roadGenScript.roadClass> HeightRoads(Vector3d pos)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.roadGens == null) return mapSettingScript.RoadsNear(pos);
            bool any = false;
            foreach (var g in map.roadGens) if (Applies(g)) { any = true; break; }
            if (!any)
            {
                var live = mapSettingScript.RoadsNear(pos);
                if (Compare) CompareHeight(map, pos, live);
                return live;
            }
            try
            {
                var list = new List<roadGenScript.roadClass>();
                foreach (var g in map.roadGens)
                {
                    if (g == null) continue;
                    var part = Applies(g) ? Near(g, pos, false, poiGenScript.alignToRoadEmu.anyRoad) : g.RoadsNear(pos, false, poiGenScript.alignToRoadEmu.anyRoad);
                    if (part != null) list.AddRange(part);
                }
                return list;
            }
            catch (Exception e) { Fail("height", e); return mapSettingScript.RoadsNear(pos); }
        }

        /// AlignNearestRoad.Candidates_ (AlignPosToRoad's RoadsNear) asks here first.
        internal static bool TryAlign(roadGenScript g, Vector3d pos, bool notJustChunks, poiGenScript.alignToRoadEmu align, out List<roadGenScript.roadClass> roads)
        {
            roads = null;
            if (!Applies(g)) return false;
            try { roads = Near(g, pos, notJustChunks, align); return true; }
            catch (Exception e) { Fail("align", e); return false; }
        }

        // ------------------------------------------------------------------ comparing with the live generator

        /// With Enabled off: the live generator's answer next to the shadow's, by the point AlignPosToRoad / the height use.
        internal static void CompareAlign(roadGenScript g, Vector3d pos, bool notJustChunks, poiGenScript.alignToRoadEmu align, List<roadGenScript.roadClass> live)
        {
            if (!Compare || Enabled || !Usable(g)) return;
            try
            {
                var pure = Near(g, pos, notJustChunks, align);
                Check("align " + align, pos, live, pure);
            }
            catch (Exception e) { Fail("compare", e); }
        }

        private static void CompareHeight(mapSettingScript map, Vector3d pos, List<roadGenScript.roadClass> live)
        {
            try
            {
                var pure = new List<roadGenScript.roadClass>();
                foreach (var g in map.roadGens)
                {
                    if (g == null || !Usable(g)) return;
                    pure.AddRange(Near(g, pos, false, poiGenScript.alignToRoadEmu.anyRoad));
                }
                var sorted = new List<roadGenScript.roadClass>(live);
                RoadTieOrder.Sort(sorted);
                Check("height", pos, sorted, pure);
            }
            catch (Exception e) { Fail("compare height", e); }
        }

        private static void Check(string what, Vector3d pos, List<roadGenScript.roadClass> live, List<roadGenScript.roadClass> pure)
        {
            Compared++;
            var a = roadGenScript.NearestPointOnRoadBone(pos, live, out var pa);
            var b = roadGenScript.NearestPointOnRoadBone(pos, pure, out var pb);
            bool same = (a == null) == (b == null) && (a == null || (a.start.Equals(b.start) && a.end.Equals(b.end) && a.roadType == b.roadType)) && pa.Equals(pb);
            if (same) return;
            Differ++;
            var d = pa - pb; d.y = 0.0;
            double m = d.magnitude;
            if (m > WorstDifferM) WorstDifferM = m;
            LastDiffer = what + " at " + Fmt(pos) + ": live " + Desc(a) + " " + Fmt(pa) + ", pure " + Desc(b) + " " + Fmt(pb) + " (" + m.ToString("0.###") + " m)";
            if (Differ <= 20) Plugin.Log.LogInfo("PureRoads differ: " + LastDiffer);
        }

        private static string Desc(roadGenScript.roadClass r) => r == null ? "none" : r.roadType + " " + Fmt(r.start) + "-" + Fmt(r.end);
        private static string Fmt(Vector3d v) => "(" + v.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," + v.z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";

        private static void Fail(string what, Exception e)
        {
            Errors++;
            LastError = what + ": " + e.Message;
            if (Errors <= 5) Plugin.Log.LogWarning("PureRoads " + what + " failed: " + e);
        }

        // ------------------------------------------------------------------ diagnostics

        public static string Stats()
        {
            int chunks = 0, roads = 0, gen = 0;
            foreach (var s in All)
                if (s.G != null)
                    foreach (var ch in s.G.chunks.Values) { chunks++; foreach (var r in ch.roads) { roads++; if (r.generated) gen++; } }
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"compare\":" + (Compare ? "true" : "false") + ",\"heightPatched\":" + HeightPatched +
                   ",\"lookups\":" + Lookups + ",\"step1\":" + Step1 + ",\"step2\":" + Step2 + ",\"step3\":" + Step3 + ",\"notFound\":" + NotFound +
                   ",\"generated\":" + Generated + ",\"pruned\":" + Pruned + ",\"chunksFinished\":" + ChunksFinished + ",\"unfinished\":" + Unfinished +
                   ",\"resets\":" + Resets + ",\"evicted\":" + Evicted + ",\"heldForBudget\":" + HeldForBudget + ",\"spawnsHeld\":" + SpawnsHeld +
                   ",\"lookupMs\":" + LookupMs.ToString("0.##", ic) + ",\"maxLookupMs\":" + MaxLookupMs.ToString("0.##", ic) +
                   ",\"shadows\":" + All.Count + ",\"shadowChunks\":" + chunks + ",\"shadowRoads\":" + roads + ",\"shadowGenerated\":" + gen +
                   ",\"compared\":" + Compared + ",\"differ\":" + Differ + ",\"worstDifferM\":" + WorstDifferM.ToString("0.###", ic) +
                   ",\"lastDiffer\":" + Json.Str(LastDiffer) + ",\"errors\":" + Errors + ",\"lastError\":" + Json.Str(LastError) + "}";
        }

        /// The generators' sizes a lookup works with.
        public static string Settings()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null) return "{\"error\":\"no map\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("{\"roadGens\":[");
            if (map.roadGens != null)
                foreach (var g in map.roadGens)
                    sb.Append("{\"chunk\":").Append(g.chunkSize.ToString(ic)).Append(",\"inner\":").Append(g.innerChunkSize.ToString(ic))
                      .Append(",\"baround\":").Append(Baround(g)).Append(",\"iaround\":").Append(Iaround(g))
                      .Append(",\"dots\":[").Append(g.minDotCount).Append(',').Append(g.maxDotCount).Append(']')
                      .Append(",\"main\":").Append(g.hasMainRoad ? "true" : "false").Append(",\"side\":").Append(g.hasSideRoad ? "true" : "false")
                      .Append(",\"lateral\":").Append(AlignNearestRoad.MaxLateral(g).ToString("0", ic))
                      .Append(",\"chunkDist\":").Append(g.chunkDist).Append(",\"live\":").Append(g.chunks.Count).Append("},");
            if (sb[sb.Length - 1] == ',') sb.Length--;
            sb.Append("],\"poiGens\":[");
            if (map.poiGens != null)
                foreach (var p in map.poiGens)
                    sb.Append("{\"chunk\":").Append(p.chunkSize.ToString(ic)).Append(",\"count\":[").Append(p.minCount).Append(',').Append(p.maxCount).Append(']')
                      .Append(",\"minDist\":").Append(p.minDistBetween.ToString(ic)).Append(",\"round\":").Append(p.round.ToString(ic))
                      .Append(",\"roadAffects\":").Append(p.roadAffects ? "true" : "false")
                      .Append(",\"place\":").Append(p.placeDistance.ToString(ic)).Append(",\"lod\":").Append(p.lodDistance.ToString(ic))
                      .Append(",\"remove\":").Append(p.removeDistance.ToString(ic)).Append(",\"sameGen\":").Append(map.roadGens != null && map.roadGens.Contains(p.roadgen) ? "true" : "false").Append("},");
            if (sb[sb.Length - 1] == ',') sb.Length--;
            return sb.Append("]}").ToString();
        }
    }
}
