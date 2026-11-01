using System;
using System.Collections.Generic;
using TLDRevamp.Net;

static class TankAddsTest
{
    static int _failed;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) _failed++; }
    static bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;

    static List<TankAdds.Add> Diff(int[] ft, float[] fa, int[] nt, float[] na) { var l = new List<TankAdds.Add>(); TankAdds.Diff(3, ft, fa, nt, na, l); return l; }

    static int Main()
    {
        var up = Diff(new[] { 1 }, new[] { 10f }, new[] { 1 }, new[] { 18f });
        Check(up.Count == 1 && up[0].Tank == 3 && up[0].Type == 1 && Near(up[0].Litres, 8f), "poured in: the change, not the level");
        var down = Diff(new[] { 1 }, new[] { 10f }, new[] { 1 }, new[] { 4f });
        Check(down.Count == 1 && Near(down[0].Litres, -6f), "poured out: a negative change");
        Check(Diff(new[] { 1 }, new[] { 10f }, new[] { 1 }, new[] { 10f }).Count == 0, "no change, no row");
        var fresh = Diff(null, null, new[] { 2 }, new[] { 5f });
        Check(fresh.Count == 1 && fresh[0].Type == 2 && Near(fresh[0].Litres, 5f), "a tank with no record is empty");
        var gone = Diff(new[] { 2 }, new[] { 5f }, new int[0], new float[0]);
        Check(gone.Count == 1 && gone[0].Type == 2 && Near(gone[0].Litres, -5f), "a fluid no longer listed all came out");
        var mix = Diff(new[] { 1 }, new[] { 10f }, new[] { 1, 2 }, new[] { 9f, 3f });
        Check(mix.Count == 2 && Near(mix[0].Litres, -1f) && mix[1].Type == 2 && Near(mix[1].Litres, 3f), "each fluid on its own");
        var dup = Diff(new[] { 1, 1 }, new[] { 2f, 3f }, new[] { 1, 1 }, new[] { 4f, 3f });
        Check(dup.Count == 1 && Near(dup[0].Litres, 2f), "a fluid listed twice counts once");

        // two players at one barrel: both changes add up, whichever comes first
        float barrel = 10f;
        foreach (var l in new[] { 8.16f, 8.02f }) barrel += TankAdds.Allowed(l, barrel, 100f - barrel);
        Check(Near(barrel, 26.18f), "two pours add up");
        Check(Near(TankAdds.Allowed(50f, 90f, 10f), 10f), "no more than the room left");
        Check(Near(TankAdds.Allowed(-50f, 20f, 80f), -20f), "no more out than it holds");
        Check(Near(TankAdds.Allowed(5f, 90f, -1f), 0f), "an overfull tank takes nothing");

        var w = new NetWriter();
        TankAdds.Write(w, up);
        var back = new List<TankAdds.Add>();
        var r = new NetReader { Buf = w.Buf, Pos = 0, End = w.Len };
        Check(TankAdds.Read(r, back) && back.Count == 1 && back[0].Tank == 3 && Near(back[0].Litres, 8f) && r.Remaining == 0, "written and read back");

        w.Reset(); w.VarU32(TankAdds.MaxAdds + 1);
        Check(!TankAdds.Read(new NetReader { Buf = w.Buf, Pos = 0, End = w.Len }, back), "too many rows are refused");
        w.Reset(); w.VarU32(1); w.VarU32(0); w.VarU32(0); w.F32(float.NaN);
        Check(!TankAdds.Read(new NetReader { Buf = w.Buf, Pos = 0, End = w.Len }, back) && back.Count == 0, "NaN is refused");
        w.Reset(); w.VarU32(2); w.VarU32(0);
        Check(!TankAdds.Read(new NetReader { Buf = w.Buf, Pos = 0, End = w.Len }, back) && back.Count == 0, "cut short is refused");

        Console.WriteLine(_failed == 0 ? "All passed." : _failed + " failed.");
        return _failed == 0 ? 0 : 1;
    }
}
