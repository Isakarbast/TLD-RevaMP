using System;
using TLDRevamp.Net;

/// Entities.TryTakeCrash's guard: a crash takeover only for a copy where its owner has the car.
static class CrashGuardTest
{
    private static int _failed;
    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failed++;
    }

    /// TelemetryPrivacy.cs's text (the source, not the class: it needs the game): a reason it doesn't list leaves bug
    /// reports as "[removed]".
    static string ReportWords = "";
    static void FindReportWords()
    {
        string root = Environment.GetEnvironmentVariable("TLD_REVAMP_ROOT");
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); root == null && d != null; d = d.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "src/TLDRevamp/TelemetryPrivacy.cs"))) root = d.FullName;
        if (root != null) ReportWords = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/TLDRevamp/TelemetryPrivacy.cs"));
    }

    static int Main()
    {
        FindReportWords();
        // bug report 2026-11-06 10:49: the host's copy of the joiner's car 141 m behind it, 20 m/s, ~100 ms display delay
        Check("a copy 141 m behind its owner's car is not taken over", !CrashGuard.CopyFits(141, 0.05, 20, 0.1, out var why, out var allow) && why == CrashGuard.Far && allow < 20);
        // a real crash: the copy trails the owner's present by speed x delay, and the crash moves it a few metres
        Check("a copy at rest beside its owner's pose is taken over", CrashGuard.CopyFits(0.5, 0.05, 0, 0.1, out _, out _));
        Check("a copy 4 m off at 10 m/s (crash push) is taken over", CrashGuard.CopyFits(4, 0.05, 10, 0.1, out _, out _));
        Check("a copy trailing 15 m at 40 m/s with 150 ms delay is taken over", CrashGuard.CopyFits(15, 0.05, 40, 0.15, out _, out _));
        Check("a copy trailing 30 m at 40 m/s with 150 ms delay is not", !CrashGuard.CopyFits(30, 0.05, 40, 0.15, out _, out _));
        // where the owner has it is not known
        Check("a moving car's pose over a second old: not taken over", !CrashGuard.CopyFits(1, 1.5, 15, 0.1, out why, out _) && why == CrashGuard.OldPose);
        // a parked car: its owner sends nothing once it rests, its last pose stays true
        Check("a parked car's minute-old pose: taken over (rear-ending a parked car)", CrashGuard.CopyFits(0.3, 60, 0, 0.1, out _, out _));
        Check("a parked car's copy 20 m from its pose: not taken over", !CrashGuard.CopyFits(20, 60, 0, 0.1, out why, out _) && why == CrashGuard.Far);
        Check("no pose at all: not taken over", !CrashGuard.CopyFits(double.NaN, double.PositiveInfinity, 0, 0.1, out why, out _) && why == CrashGuard.NoPose);
        Check("an infinite age while moving: not taken over", !CrashGuard.CopyFits(1, double.PositiveInfinity, 5, 0.1, out _, out _));
        Check("a negative delay counts as none", CrashGuard.CopyFits(8, 0, 0, -1, out _, out _) && !CrashGuard.CopyFits(9, 0, 0, -1, out _, out _));
        Check("a 4,383 m/s contact (a copy snapped back) is no crash", !CrashGuard.SpeedFits(4382.9));
        Check("a head-on at 2 x 200 km/h is a crash", CrashGuard.SpeedFits(111));
        Check("a NaN contact speed is no crash", !CrashGuard.SpeedFits(double.NaN));
        if (ReportWords.Length == 0) Console.WriteLine("SKIP report words: repo root not found");
        else
        {
            foreach (var reason in new[] { CrashGuard.NoPose, CrashGuard.OldPose, CrashGuard.Far, CrashGuard.TooFast })
                Check("reason kept in bug reports: " + reason, ReportWords.Contains("\"" + reason + "\""));
            Check("event kept in bug reports: crash-stale", ReportWords.Contains(" crash-stale "));
        }
        Console.WriteLine(_failed == 0 ? "PASS" : "FAIL");
        return _failed == 0 ? 0 : 1;
    }
}
