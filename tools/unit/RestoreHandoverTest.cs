using System;
using System.Collections.Generic;
using System.Linq;
using TLDRevamp.Net;

static class RestoreHandoverTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static (List<uint> kept, int back) Owed(uint[] handed, int sent, params uint[] restored) =>
        RestoreHandover.StillOwed(handed.ToList(), x => x, sent, new HashSet<uint>(restored));

    static int Main()
    {
        var all = Owed(new uint[] { 1, 2, 3 }, 3, 1, 2, 3);
        Check("everything came back: nothing owed", all.kept.Count == 0 && all.back == 3);
        var one = Owed(new uint[] { 1, 2, 3 }, 3, 1, 3);
        Check("one didn't spawn: that one stays owed", one.kept.SequenceEqual(new uint[] { 2 }) && one.back == 2);
        var none = Owed(new uint[] { 1, 2 }, 2);
        Check("nothing came back: everything stays owed", none.kept.SequenceEqual(new uint[] { 1, 2 }) && none.back == 0);
        var unsent = Owed(new uint[] { 1, 2, 3 }, 2, 1, 2);
        Check("what never went out stays owed", unsent.kept.SequenceEqual(new uint[] { 3 }) && unsent.back == 2);
        var stray = Owed(new uint[] { 1, 2 }, 1, 2);
        Check("a confirmation of what never went out counts for nothing", stray.kept.SequenceEqual(new uint[] { 1, 2 }) && stray.back == 0);
        return failures == 0 ? 0 : 1;
    }
}
