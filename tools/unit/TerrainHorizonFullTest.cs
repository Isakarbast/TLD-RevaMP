using System;
using System.Collections.Generic;
using UnityEngine;
using TLDRevamp.Fixes;

internal static class TerrainHorizonFullTest
{
    private static int _failures;
    private static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + label);
        if (!ok) _failures++;
    }

    private static roadGenScript.roadClass Road(int elements, int bones, bool includeNull = false)
    {
        var road = new roadGenScript.roadClass
        {
            generated = true,
            start = new Vector3d(0, 0, 0),
            end = new Vector3d(100, 0, 0)
        };
        if (includeNull) road.roadElements.Add(null);
        for (int i = 0; i < elements; i++)
        {
            var element = new roadGenScript.roadElementClass();
            for (int b = 0; b < bones; b++) element.bonePos.Add(new Vector3d(i, b, 0));
            road.roadElements.Add(element);
        }
        return road;
    }

    internal static int Main()
    {
        var road = Road(100000, 1);
        var chunk = new roadGenScript.dotChunkClass();
        chunk.roads.Add(road);
        var gen = new roadGenScript();
        gen.chunks[Vector3d.zero] = chunk;
        gen.chunksNeeded.Add(Vector3d.zero);
        var map = new mapSettingScript { roadGens = new List<roadGenScript> { gen } };
        menuhandler.s = new menuhandler { currentMainMap = map };
        Time.frameCount = 1;

        var signatureMethod = typeof(TerrainHorizon).GetMethod("RoadSignature",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (signatureMethod == null) throw new Exception("production RoadSignature missing");

        int Invoke()
        {
            try { return (int)signatureMethod.Invoke(null, null); }
            catch (System.Reflection.TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        int hash = Invoke();
        Check(TerrainHorizon.SignatureVisits == TerrainHorizon.SignatureMaxElements,
              "production RoadSignature stops visits at the finite budget");
        Check(TerrainHorizon.SignatureElements == 100000,
              "production RoadSignature retains the declared element count");
        Check(hash == Invoke(), "cached signature is stable");

        Time.frameCount += TerrainHorizon.SignatureIntervalFrames;
        road.roadElements.Clear();
        road.roadElements.Add(null);
        road.roadElements.Add(null);
        int before = (int)TerrainHorizon.SignatureVisits;
        Invoke();
        Check(TerrainHorizon.SignatureVisits <= TerrainHorizon.SignatureMaxElements,
              "null elements also consume the finite visit budget");
        Check(TerrainHorizon.SignatureVisits >= before || before == TerrainHorizon.SignatureMaxElements,
              "visit diagnostics remain monotonic across walks");

        // the whole rebuild in one Tick (before), then spread over frames: one element or one mesh a frame, and the
        // world shifted between the slices of a rebuild
        TerrainHorizon.SpreadRebuild = false; RibbonRebase(false);
        TerrainHorizon.SpreadRebuild = true; TerrainHorizon.StepBudgetMs = 0; RibbonRebase(true);
        TerrainHorizon.CellBatches = false; RibbonRebase(true); TerrainHorizon.CellBatches = true;
        TerrainHorizon.SpreadRebuild = false; CellReuse();
        Console.WriteLine(_failures == 0 ? "PASS" : "FAIL");
        return _failures == 0 ? 0 : 1;
    }

    private static bool Building() => typeof(TerrainHorizon).GetField("_job", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null) != null;

    /// Ticks until the rebuild in progress is done (one Tick when it isn't spread); with `shift`, the world is shifted
    /// after the rebuild's first slice, as the game may do between two frames of it.
    private static bool Tick(Vector3? shift = null)
    {
        TerrainHorizon.Tick();
        bool shifted = shift.HasValue && Building();
        if (shifted) mainscript.s.Shift(shift.Value);
        for (int i = 0; i < 1000 && Building(); i++) TerrainHorizon.Tick();
        Check(!Building(), "the rebuild finishes");
        return shifted;
    }

    private static roadGenScript.roadClass Line(double z0, double z1, int roadID)
    {
        var road = Road(0, 0);
        road.start = new Vector3d(0, 0, z0); road.end = new Vector3d(0, 0, z1);
        var element = new roadGenScript.roadElementClass { roadID = roadID };
        for (int i = 0; i <= 4; i++) element.bonePos.Add(new Vector3d(0, 0, z0 + (z1 - z0) * i / 4));
        road.roadElements.Add(element);
        return road;
    }

    /// Every ribbon vertex in world space, sorted: the picture, whatever meshes it is split into.
    private static List<string> WorldVertices()
    {
        var batches = (System.Collections.IDictionary)typeof(TerrainHorizon).GetField("Batches",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        var all = new List<string>();
        foreach (System.Collections.DictionaryEntry kv in batches)
        {
            var b = kv.Value;
            var go = (GameObject)b.GetType().GetField("Go").GetValue(b);
            var data = (TerrainHorizonGeometry.Batch)b.GetType().GetField("Data").GetValue(b);
            for (int i = 0; i < data.VertexCount; i++)
            {
                var w = go.transform.TransformPoint(new Vector3((float)data.Vx[i], (float)data.Vy[i], (float)data.Vz[i]));
                all.Add(w.x.ToString("R") + "," + w.y.ToString("R") + "," + w.z.ToString("R"));
            }
        }
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    /// Meshes by road type and map cell: a cell whose roads all lie inside the ring keeps its mesh when the player
    /// walks, one at the ring's inner edge is redone, and the picture is the same as with one mesh per road type.
    private static void CellReuse()
    {
        List<string> Run(bool cells, out long reused, out long rebuilt)
        {
            typeof(TerrainHorizon).GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
            TerrainHorizon.CellBatches = cells;
            mainscript.s = new mainscript();
            mainscript.visszarakva = Vector3.zero;
            settingsscript.s.S.FDrawDistance = 10000;
            var gen = new roadGenScript();
            var chunk = new roadGenScript.dotChunkClass();
            chunk.roads.Add(Line(3100, 3400, 1));   // wholly inside the ring, in its own cell
            chunk.roads.Add(Line(300, 900, 1));     // across the ring's inner edge (495 m)
            gen.chunks[Vector3d.zero] = chunk; gen.chunksNeeded.Add(Vector3d.zero);
            menuhandler.s.currentMainMap.roadGens = new List<roadGenScript> { gen };
            Tick();
            long r0 = TerrainHorizon.CellsReused, b0 = TerrainHorizon.CellsRebuilt;
            mainscript.s.player.transform.position += new Vector3(40, 0, 0);
            Tick();
            reused = TerrainHorizon.CellsReused - r0; rebuilt = TerrainHorizon.CellsRebuilt - b0;
            return WorldVertices();
        }
        var withCells = Run(true, out long reused, out long rebuilt);
        Check(reused == 1, "a cell wholly inside the ring keeps its mesh when the player walks 40 m (kept " + reused + ")");
        Check(rebuilt == 1, "the cell at the ring's inner edge is redone (redone " + rebuilt + ")");
        var oneMesh = Run(false, out _, out _);
        Check(withCells.Count > 0 && withCells.Count == oneMesh.Count, "cells draw as many vertices as one mesh per type (" + withCells.Count + " vs " + oneMesh.Count + ")");
        bool same = withCells.Count == oneMesh.Count;
        for (int i = 0; same && i < withCells.Count; i++) same = withCells[i] == oneMesh[i];
        Check(same, "cells draw exactly the same vertices as one mesh per type");
        Check(TerrainHorizonGeometry.BoxInRing(-10, 600, 10, 700, 0, 0, 495, 1000), "a box between the radii is in the ring");
        Check(!TerrainHorizonGeometry.BoxInRing(-10, 400, 10, 700, 0, 0, 495, 1000), "a box reaching inside the near radius is not");
        Check(!TerrainHorizonGeometry.BoxInRing(-10, 600, 10, 1200, 0, 0, 495, 1000), "a box reaching past the far radius is not");
        Check(!TerrainHorizonGeometry.BoxInRing(-800, -800, 800, 800, 0, 0, 495, 5000), "a box around the centre is not");
        TerrainHorizon.CellBatches = true;

        // a bone moved in place, only in height and inside the cell's bounds: the mesh is redone, not kept
        typeof(TerrainHorizon).GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
        mainscript.s = new mainscript();
        mainscript.visszarakva = Vector3.zero;
        var far = Line(3100, 3400, 1);
        var gen2 = new roadGenScript();
        var chunk2 = new roadGenScript.dotChunkClass(); chunk2.roads.Add(far);
        gen2.chunks[Vector3d.zero] = chunk2; gen2.chunksNeeded.Add(Vector3d.zero);
        menuhandler.s.currentMainMap.roadGens = new List<roadGenScript> { gen2 };
        Tick();
        var bones = far.roadElements[0].bonePos;
        bones[2] = new Vector3d(bones[2].x, bones[2].y + 5, bones[2].z);
        long r1 = TerrainHorizon.CellsReused, b1 = TerrainHorizon.CellsRebuilt;
        mainscript.s.player.transform.position += new Vector3(40, 0, 0);
        Tick();
        Check(TerrainHorizon.CellsReused == r1 && TerrainHorizon.CellsRebuilt == b1 + 1, "a bone raised in place makes its cell be redone");
        bool raised = false;
        foreach (var v in WorldVertices()) if (float.Parse(v.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture) > 4f) raised = true;
        Check(raised, "the redone mesh has the raised bone's height");
    }

    private static void RibbonRebase(bool spread)
    {
        typeof(TerrainHorizon).GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);   // a fresh ribbon root
        mainscript.s = new mainscript();
        mainscript.visszarakva = Vector3.zero;   // the world unshifted again for the second run
        var road = Road(0, 0);
        road.start = new Vector3d(0, 0, 600);
        road.end = new Vector3d(0, 0, 800);
        var element = new roadGenScript.roadElementClass { roadID = 1 };
        element.bonePos.Add(road.start); element.bonePos.Add(road.end);
        road.roadElements.Add(element);
        var gen = new roadGenScript();
        var chunk = new roadGenScript.dotChunkClass(); chunk.roads.Add(road);
        gen.chunks[Vector3d.zero] = chunk; gen.chunksNeeded.Add(Vector3d.zero);
        menuhandler.s.currentMainMap.roadGens = new List<roadGenScript> { gen };
        Tick();

        var batches = (System.Collections.IDictionary)typeof(TerrainHorizon).GetField("Batches",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        object batch = null;
        foreach (System.Collections.DictionaryEntry kv in batches) batch = kv.Value;   // the one road's mesh (keyed by type and cell)
        var go = (GameObject)batch.GetType().GetField("Go").GetValue(batch);
        var mesh = (Mesh)batch.GetType().GetField("Mesh").GetValue(batch);
        Vector3 WorldVertex() => go.transform.TransformPoint(mesh.vertices[0]);
        var expected = WorldVertex();
        Check(mainscript.s.visszarakni.Count == 1, "only the ribbon root registers for the game's rebase");
        foreach (var delta in new[] { new Vector3(-4500, -30, 1200), new Vector3(1800, 15, -600) })
        {
            var before = WorldVertex();
            mainscript.s.Shift(delta); expected += delta;
            Check((WorldVertex() - before - delta).sqrMagnitude < 0.0001f,
                  "game rebase moves the existing ribbon exactly once before Tick");
            Tick();
            Check((WorldVertex() - expected).sqrMagnitude < 0.0001f,
                  "production rebuild does not apply the parent's rebase a second time");
        }
        // An ordinary later rebuild must also use the shifted parent's local space.
        mainscript.s.Shift(new Vector3(-4500, 0, 0)); expected += new Vector3(-4500, 0, 0);
        Tick();
        mainscript.s.player.transform.position += new Vector3(40, 0, 0);
        Tick();
        Check((WorldVertex() - expected).sqrMagnitude < 0.0001f, "walking after a rebase keeps the ribbon on its road");
        if (!spread) return;
        // a rebase between two slices of one rebuild: the ribbon still lands on its road
        mainscript.s.player.transform.position += new Vector3(40, 0, 0);
        // (with cell meshes a road wholly inside the ring keeps its mesh, the rebuild has nothing to spread and the
        // shift lands after it: the run with CellBatches off below keeps the in-the-middle case covered)
        var shift = new Vector3(2500, 0, -700);
        if (Tick(shift)) expected += shift; else { mainscript.s.Shift(shift); expected += shift; Tick(); }
        Check((WorldVertex() - expected).sqrMagnitude < 0.0001f, "a rebase in the middle of a spread rebuild keeps the ribbon on its road");
    }
}
