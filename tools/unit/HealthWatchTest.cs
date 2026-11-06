using System;
using System.Collections.Generic;
using TLDRevamp.Net;

/// The background watches' rules (HealthWatchRules): when a problem has lasted long enough to report, and what can never
/// be right.
static class HealthWatchTest
{
    private static int _failed;
    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failed++;
    }

    static string Root()
    {
        string root = Environment.GetEnvironmentVariable("TLD_REVAMP_ROOT");
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); root == null && d != null; d = d.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "src/TLDRevamp/TelemetryPrivacy.cs"))) root = d.FullName;
        return root;
    }

    static int Main()
    {
        var p = new HealthWatchRules.Persist();
        var body = new HashSet<string> { "7:" + HealthWatchRules.Body };
        var none = new HashSet<string>();
        Check("a problem seen once is not due yet", p.Pass("7:", body, 0).Count == 0);
        Check("still not due before AfterS", p.Pass("7:", body, HealthWatchRules.AfterS - 1).Count == 0);
        Check("due once it lasted AfterS", p.Pass("7:", body, HealthWatchRules.AfterS).Count == 1);
        Check("and active (the tab shows it)", p.Active("7:" + HealthWatchRules.Body));
        Check("not due again while it lasts", p.Pass("7:", body, HealthWatchRules.AfterS + 30).Count == 0);
        p.Pass("7:", none, HealthWatchRules.AfterS + 31);
        Check("cleared: no longer active", !p.Active("7:" + HealthWatchRules.Body));
        p.Pass("7:", body, HealthWatchRules.AfterS + 32);
        Check("back soon after: not reported again within RepeatS", p.Pass("7:", body, HealthWatchRules.AfterS * 3 + 40).Count == 0);
        double later = HealthWatchRules.AfterS + HealthWatchRules.RepeatS + 50;
        p.Pass("7:", none, later); p.Pass("7:", body, later + 1);
        Check("back after RepeatS, lasting AfterS: reported again", p.Pass("7:", body, later + 1 + HealthWatchRules.AfterS).Count == 1);

        var q = new HealthWatchRules.Persist();
        q.Pass("1:", new HashSet<string> { "1:x" }, 0);
        q.Pass("12:", new HashSet<string> { "12:x" }, 0);
        q.Pass("1:", none, 1);
        Check("one object's pass leaves another's alone (1: vs 12:)", q.Pass("12:", new HashSet<string> { "12:x" }, HealthWatchRules.AfterS).Count == 1);
        Check("a problem that flickers off and on starts over", q.Pass("1:", new HashSet<string> { "1:x" }, HealthWatchRules.AfterS).Count == 0);
        var r = new HealthWatchRules.Persist();
        r.Pass("", new HashSet<string> { "3:pos" }, 0, k => 10f);
        Check("a longer wait per key is kept", r.Pass("", new HashSet<string> { "3:pos" }, 8, k => 10f).Count == 0 && r.Pass("", new HashSet<string> { "3:pos" }, 10, k => 10f).Count == 1);

        Check("a runaway is reported on sight, a copy behind its owner only after longer",
              HealthWatchRules.AfterOf("4:" + HealthWatchRules.Runaway) == 0 && HealthWatchRules.AfterOf("4:" + HealthWatchRules.NonFinite) == 0
              && HealthWatchRules.AfterOf("4:" + HealthWatchRules.Position) > HealthWatchRules.AfterOf("4:" + HealthWatchRules.Body));
        var z = new HealthWatchRules.Persist();
        Check("due on the first pass when it must last 0 s", z.Pass("4:", new HashSet<string> { "4:" + HealthWatchRules.Runaway }, 0, HealthWatchRules.AfterOf).Count == 1);
        Check("a normal speed is fine", !HealthWatchRules.IsRunaway(30, -5, 2));
        Check("2000 km/h is runaway", HealthWatchRules.IsRunaway(560, 0, 0));
        Check("NaN velocity is runaway", HealthWatchRules.IsRunaway(double.NaN, 0, 0));
        Check("NaN position is not finite", !HealthWatchRules.Finite(1, double.NaN, 2) && !HealthWatchRules.Finite(double.PositiveInfinity, 0, 0) && HealthWatchRules.Finite(1e5, -40, 3));
        Check("20 m under the ground with nothing below: under the ground", HealthWatchRules.IsUnderGround(-20, 0, false));
        Check("under the ground but standing on something (a cellar): fine", !HealthWatchRules.IsUnderGround(-20, 0, true));
        Check("a wheel sunk 1 m: fine", !HealthWatchRules.IsUnderGround(-1, 0, false));
        Check("no ground over it (not loaded): fine", !HealthWatchRules.IsUnderGround(-200, double.NaN, false));
        Check("item reasons", HealthWatchRules.ItemWhy(2, 2) == HealthWatchRules.ItemHeldLayer && HealthWatchRules.ItemWhy(0, 0) == HealthWatchRules.ItemOff && HealthWatchRules.ItemWhy(3, 0) == HealthWatchRules.ItemNotPickup);
        Check("an owner who left is given time to be replaced", HealthWatchRules.OwnerLeftAfterS >= 10f);

        string root = Root();
        if (root == null) Console.WriteLine("SKIP report words: repo root not found");
        else
        {
            string privacy = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/TLDRevamp/TelemetryPrivacy.cs"));
            string panel = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/TLDRevamp/ObjectPanelEvents.cs"));
            foreach (var w in new[] { HealthWatchRules.Body, HealthWatchRules.Position, HealthWatchRules.NonFinite, HealthWatchRules.Runaway, HealthWatchRules.UnderGround,
                                      HealthWatchRules.OwnerLeft, HealthWatchRules.SlotMismatch, HealthWatchRules.RepairLoop, HealthWatchRules.ItemOff, HealthWatchRules.ItemHeldLayer, HealthWatchRules.ItemNotPickup })
            {
                Check("reason kept in bug reports: " + w, privacy.Contains("\"" + w + "\""));
                Check("reason shown in item health: " + w, panel.Contains("\"" + w + "\""));
            }
            foreach (var k in new[] { "health-problem", "item-untargetable", "census-repair-failed" })
            {
                Check("event kept in bug reports: " + k, privacy.Contains(" " + k + " "));
                Check("event shown in item health: " + k, panel.Contains("\"" + k + "\""));
            }
        }
        Console.WriteLine(_failed == 0 ? "PASS" : "FAIL");
        return _failed == 0 ? 0 : 1;
    }
}
