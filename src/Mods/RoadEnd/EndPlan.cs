using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoadEnd
{
    /// Where the main road ends: the configured distance driven along the main road from the start, not as the crow
    /// flies (the game's main road wanders, more in newer versions, so the two differ by thousands of km).
    ///
    /// The main road runs through 10 km road chunks along x = 0 (roadGenScript, chunkSize 10000). Inside a chunk it
    /// goes from its southmost dot to its northmost through the chunk's other dots, on a path the game picks at random
    /// from the chunk's seed (ConnectDots); between chunks a link road joins one chunk's northmost dot to the next
    /// one's southmost. All of it is worked out once per world from the game's own code, on scratch copies of the
    /// chunks (the game's own are put back untouched): the path through each chunk, piece by piece, until the
    /// pieces add up to the distance. The end is a place on one piece: step Step of chunk Chunk on the drive (Link:
    /// the link road after it), Along metres into it. Roads.Trim keeps what comes before it on the drive.
    ///
    /// Chunks count along the drive (World.Sign): chunk t is the one at z = Sign * t * chunkSize; steps count from
    /// the side of a chunk the drive comes in at.
    public static class EndPlan
    {
        public const int Link = int.MaxValue;

        public struct State
        {
            public bool Known;          // false: not worked out yet (no world): nothing is cut
            public int Chunk, Step;     // the piece the road ends on (Link: the link road after chunk Chunk)
            public double Along;        // metres into it, on the drive
            public double Cut;          // u of the planned end (for the probe's checks of what lies past it)
            public Vector3d Planned;    // the planned end (on the straight line between the piece's dots)
            public Vector3d Approach;   // a point on the road 400 m before it (tests)
            public double CrowKm;       // how far the end is from the start in a straight line
            public bool HasRoadEnd;
            public Vector3d RoadEnd;    // where the main road stops (global), once that piece is made
            public Vector3d RoadDir;    // its direction there (the drive's, flat)
        }

        public static State Current;
        public static event Action Changed;
        public static int ChunksMeasured;
        public static float MeasureMs;

        public static void Reset()
        {
            Current = new State { Cut = double.MaxValue };
            Roads.Trimmed = Roads.Dropped = 0;
            Changed?.Invoke();
        }

        /// One piece of the main road on the drive: from A to B (its dots), Len metres of road as the game lays it.
        public struct Piece { public Vector3d A, B; public double Len; }

        /// The game's own chunks at `keys` taken out, scratch ones made in their place from the same seeds (the same
        /// dots), `use` run on them, the game's put back.
        private static T Scratch<T>(roadGenScript rg, Vector3d[] keys, Func<T> use)
        {
            var own = new roadGenScript.dotChunkClass[keys.Length];
            for (int i = 0; i < keys.Length; i++) if (rg.chunks.TryGetValue(keys[i], out own[i])) rg.chunks.Remove(keys[i]);
            try
            {
                foreach (var k in keys) rg.UpdDotChunk(k);
                return use();
            }
            finally
            {
                for (int i = 0; i < keys.Length; i++) { rg.chunks.Remove(keys[i]); if (own[i] != null) rg.chunks[keys[i]] = own[i]; }
            }
        }

        /// The main road's pieces inside the chunk at `key`, south to north, as the game makes them: its dots
        /// (UpdDotChunk) and the path it picks through them (ConnectDots), both from the chunk's seed alone, and each
        /// piece's length as roadClass.Generate lays it (Length). Null if it has none.
        public static List<Piece> PiecesOf(roadGenScript rg, Vector3d key) => Scratch(rg, new[] { key }, () =>
        {
            if (!rg.chunks.TryGetValue(key, out var tmp) || tmp.dots.Count == 0) return null;
            var list = new List<Piece>();
            if (tmp.dots.Count > 1) rg.ConnectDots(key, key, tmp.dots, tmp.dots, null, key);
            foreach (int ri in tmp.mainRoad)
            {
                var r = tmp.roads[ri];
                list.Add(new Piece { A = tmp.dots[r.id1].pos, B = tmp.dots[r.id2].pos, Len = Length(rg, r) });
            }
            if (list.Count == 0) list.Add(new Piece { A = tmp.dots[0].pos, B = tmp.dots[0].pos, Len = 0 });
            return list;
        });

        /// The link road from the chunk at `south` to the one north of it (its northmost dot to the other's southmost).
        public static Piece? LinkOf(roadGenScript rg, Vector3d south, Vector3d north) => Scratch(rg, new[] { south, north }, () =>
        {
            if (!rg.chunks.TryGetValue(south, out var s) || !rg.chunks.TryGetValue(north, out var n) || s.dots.Count == 0 || n.dots.Count == 0) return (Piece?)null;
            int i1 = s.dots.Count - 1;
            var r = new roadGenScript.roadClass(south, i1, north, 0, s.dots[i1].pos, n.dots[0].pos, rg.terrainAlignMinDist, rg.terrainAlignMaxDist, south, 0, roadGenScript.roadTypeEmu.mainRoad);
            return new Piece { A = s.dots[i1].pos, B = n.dots[0].pos, Len = Length(rg, r) };
        });

        /// How long a road is as roadClass.Generate lays it, without making it: 10 m steps along the straight line
        /// between its dots, each pulled sideways and onto the ground by the game's PointOnRoad (its noise), a straight
        /// 10 m more at every 10th step (where Generate starts a new piece), and the heights evened out between the two
        /// dots' as Generate does. Both its chunks must be in rg.chunks.
        public static double Length(roadGenScript rg, roadGenScript.roadClass r)
        {
            var pos = rg.chunks[r.chunk1].dots[r.id1].pos;
            var pos2 = rg.chunks[r.chunk2].dots[r.id2].pos;
            var flat = pos2 - pos; flat.y = 0;
            double mag = flat.magnitude, step = 10.0;
            var dir = flat.normalized * step;
            var pts = new List<Vector3d> { pos };
            var v = pos;
            int inPiece = 1;
            for (int i = 0; i < 1000000; i++)
            {
                var prev = v;
                v = rg.PointOnRoad(r, prev + dir, true);
                v = prev + (v - prev).normalized * step;
                var d = v - pos; d.y = 0;
                double m2 = d.magnitude;
                if (m2 >= mag - step * 2.0) { pts.Add(pos2); break; }
                pts.Add(v); inPiece++;
                if (inPiece > 10) { v += v - prev; pts.Add(v); inPiece = 2; }
                if (m2 >= mag) break;
            }
            if (rg.temporaryHeightSmoothing)
            {
                double lo = Math.Min(pos.y, pos2.y), hi = Math.Max(pos.y, pos2.y);
                if (lo != hi)
                {
                    double mid = (pos.y + pos2.y) * 0.5, span = hi - lo;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var p = pts[i];
                        p.y = Mathd.Lerp(mid - span * 0.5, mid + span * 0.5, Mathd.InverseLerp(lo, hi, p.y));
                        pts[i] = p;
                    }
                }
            }
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += Dist(pts[i - 1], pts[i]);
            return len;
        }

        /// Called after the game made its road chunks this frame: works the end out once per world.
        public static void Update(roadGenScript rg)
        {
            if (!World.Active || rg == null || !rg.hasMainRoad || Current.Known || rg.mapSetting == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double want = World.Km * 1000.0, cs = rg.chunkSize, done = 0;
            int sign = World.Sign;
            ChunksMeasured = 0;
            Vector3d Key(int t) => new Vector3d(0, 0, sign * t * cs);
            List<Piece> Travel(int t)
            {
                var p = PiecesOf(rg, Key(t));
                ChunksMeasured++;
                if (p != null && sign < 0) { p.Reverse(); for (int i = 0; i < p.Count; i++) p[i] = new Piece { A = p[i].B, B = p[i].A, Len = p[i].Len }; }
                return p;
            }
            var path = Travel(0);
            if (path == null) return;
            // the drive starts where the road passes the start (the world's origin): that piece counts from there
            int from = 0; double into = 0, best = double.MaxValue;
            for (int i = 0; i < path.Count; i++)
            {
                double t = Along(path[i].A, path[i].B, Vector3d.zero, out double d);
                double straight = Dist(path[i].A, path[i].B);
                if (d < best) { best = d; from = i; into = straight > 0 ? t / straight * path[i].Len : 0; }
            }
            int maxChunks = (int)Math.Ceiling(want / cs) + 10;
            for (int t = 0; t < maxChunks; t++)
            {
                if (t > 0) { path = Travel(t); if (path == null) return; from = 0; into = 0; }
                for (int i = from; i < path.Count; i++)
                {
                    double skip = i == from ? into : 0;
                    double len = path[i].Len - skip;
                    if (done + len >= want) { Found(t, i, skip + want - done, path[i], path, i, sw); return; }
                    done += len;
                }
                // the link road to the next chunk on the drive
                var south = sign > 0 ? Key(t) : Key(t + 1);
                var north = sign > 0 ? Key(t + 1) : Key(t);
                var link = LinkOf(rg, south, north);
                if (link == null) return;
                var lk = link.Value;
                if (sign < 0) lk = new Piece { A = lk.B, B = lk.A, Len = lk.Len };
                if (done + lk.Len >= want) { Found(t, Link, want - done, lk, path, path.Count, sw); return; }
                done += lk.Len;
            }
            Plugin.Log.LogWarning($"no end found within {maxChunks} road chunks ({done / 1000.0:0} km of road): the road goes on");
            Current.Known = true; Current.Cut = double.MaxValue; Current.Chunk = int.MaxValue;
        }

        private static void Found(int chunk, int step, double along, Piece piece, List<Piece> path, int i, System.Diagnostics.Stopwatch sw)
        {
            // where on the straight line between its dots (the road wanders about it): in the share of its length
            Vector3d On(Piece p, double m) => p.Len > 0 ? p.A + (p.B - p.A) * Math.Max(0, Math.Min(1, m / p.Len)) : p.A;
            var at = On(piece, along);
            // 400 m of road back along the drive, for tests
            Vector3d back; double left = 400;
            if (along >= left) back = On(piece, along - left);
            else
            {
                left -= along; back = piece.A;
                for (int j = i - 1; j >= 0 && left > 0; j--)
                {
                    if (path[j].Len >= left) { back = On(path[j], path[j].Len - left); left = 0; }
                    else { left -= path[j].Len; back = path[j].A; }
                }
            }
            Current.Chunk = chunk; Current.Step = step; Current.Along = along;
            Current.Planned = at; Current.Approach = back; Current.Cut = U(at);
            var flat = at; flat.y = 0; Current.CrowKm = flat.magnitude / 1000.0;
            Current.Known = true;
            MeasureMs = (float)sw.Elapsed.TotalMilliseconds;
            Changed?.Invoke();
            Plugin.Log.LogInfo($"the main road ends after {World.Km:0.###} km of road, in road chunk {chunk} ({(step == Link ? "the link road after it" : "piece " + step)}), " +
                               $"{Current.CrowKm:0.0} km from the start as the crow flies; {ChunksMeasured} chunks measured in {MeasureMs:0} ms. The land and the side roads go on");
        }

        /// Where a main road comes on the drive: its chunk and step as in State (false: not a main road this knows).
        public static bool Key(roadGenScript.roadClass road, roadGenScript rg, out int chunk, out int step)
        {
            chunk = 0; step = 0;
            if (road == null || rg == null || road.roadType != roadGenScript.roadTypeEmu.mainRoad) return false;
            double cs = rg.chunkSize;
            int c1 = (int)Math.Round(road.chunk1.z / cs), c2 = (int)Math.Round(road.chunk2.z / cs);
            if (c1 != c2)
            {
                // a link road (the game turns main roads to run north: chunk1 is its south end)
                int south = Math.Min(c1, c2), north = Math.Max(c1, c2);
                chunk = World.Sign > 0 ? south : -north;
                step = Link;
                return true;
            }
            if (!rg.chunks.TryGetValue(road.selfChunk, out var ch)) return false;
            // the chunk's main road list holds its path's pieces in order, and the link roads stored in it as well
            // (at either end, by when the game joined the chunks): count the chunk's own pieces only
            int i = -1, m = 0;
            foreach (int ri in ch.mainRoad)
            {
                if (ri < 0 || ri >= ch.roads.Count) continue;
                var r = ch.roads[ri];
                if (!r.chunk1.Equals(r.chunk2)) continue;
                if (ri == road.selfID) i = m;
                m++;
            }
            if (i < 0) return false;
            chunk = World.Sign * c1;
            step = World.Sign > 0 ? i : m - 1 - i;
            return true;
        }

        /// -1: the road comes before the end on the drive (kept), 0: the road the end is on, 1: after it (goes).
        public static int Compare(int chunk, int step)
        {
            if (!Current.Known) return -1;
            if (chunk != Current.Chunk) return chunk < Current.Chunk ? -1 : 1;
            if (step != Current.Step) return step < Current.Step ? -1 : 1;
            return 0;
        }

        public static void SetRoadEnd(Vector3d at, Vector3d dir)
        {
            if (Current.HasRoadEnd && (Current.RoadEnd - at).magnitude < 0.5) return;
            Current.RoadEnd = at;
            dir.y = 0;
            Current.RoadDir = dir.magnitude > 1e-6 ? dir.normalized : new Vector3d(0, 0, World.Sign);
            Current.HasRoadEnd = true;
            Changed?.Invoke();
        }

        public static double U(Vector3d p) => World.Sign * p.z;

        public static double Dist(Vector3d a, Vector3d b) => (b - a).magnitude;

        /// How far along a..b (metres from a) the point nearest to p is (flat), and how far p is from it.
        private static double Along(Vector3d a, Vector3d b, Vector3d p, out double dist)
        {
            var ab = b - a; ab.y = 0; var ap = p - a; ap.y = 0;
            double l2 = ab.x * ab.x + ab.z * ab.z;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, (ap.x * ab.x + ap.z * ab.z) / l2)) : 0;
            var d = ap - ab * t;
            dist = d.magnitude;
            return t * Dist(a, b);
        }
    }
}
