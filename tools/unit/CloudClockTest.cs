using System;
using TLDRevamp.Fixes;

static class CloudClockTest
{
    private static int _failed;
    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failed++;
    }
    private static bool Near(double a, double b, double eps = 0.000001) => Math.Abs(a - b) < eps;
    /// distance between two places on the loop (the timer wraps from 0 back to -1)
    private static double Loop(double a, double b) { double d = (a - b) % 1; if (d < 0) d += 1; return Math.Min(d, 1 - d); }
    private static int Main()
    {
        const double speed = 0.0003, day = 1500;   // the beta's scene values
        double p0 = CloudClockMath.Phase(0.5, speed), p1 = CloudClockMath.Phase(123456.7, speed), p2 = CloudClockMath.Phase(-2000, speed);
        Check("phase stays in the game's range (-1, 0]", p0 <= 0 && p0 > -1 && p1 <= 0 && p1 > -1 && p2 <= 0 && p2 > -1);
        Check("same world total, same phase (any machine)", Near(CloudClockMath.Phase(4321.5, speed), CloudClockMath.Phase(4321.5, speed)));
        Check("advances at the game's speed", Near(CloudClockMath.Phase(1100.5, speed) - CloudClockMath.Phase(1000.5, speed), 0.03));
        Check("crosses its loop without a jump", Loop(CloudClockMath.Phase(1 / speed - 0.01, speed), CloudClockMath.Phase(1 / speed + 0.01, speed)) < 0.0001);
        Check("no speed: no wobble", CloudClockMath.Phase(100, 0) == 0);
        Check("hosting starts where the clouds are", Near(CloudClockMath.Phase(CloudClockMath.TotalFor(-0.8242, speed), speed), -0.8242, 1e-9));

        // a client takes the host's total: the host was at 100 s of the day with total 5000 when it sent, 0.5 s ago
        double here = CloudClockMath.HostTotalNow(5000, 100, 10, 1, 10.5, 100.5, day);
        Check("host total projected to now", Near(here, 5000.5));
        Check("this machine a little ahead counts", Near(CloudClockMath.HostTotalNow(5000, 100, 10, 1, 10.5, 100.7, day), 5000.7));
        Check("across midnight", Near(CloudClockMath.HostTotalNow(9000, 1499.8, 10, 1, 10.5, 0.3, day), 9000.5));
        Check("a stamp from the future doesn't rewind", Near(CloudClockMath.HostTotalNow(5000, 100, 11, 1, 10, 100, day), 5000));
        Check("a paused host doesn't advance", Near(CloudClockMath.HostTotalNow(5000, 100, 10, 0, 12, 100, day), 5000));

        // the sleep's curve between the sleeper's reports
        Check("the report itself", Near(CloudClockMath.SleepCurve(0.25, 0, 0.125), 0.25));
        Check("runs on between reports", Near(CloudClockMath.SleepCurve(0.25, 2, 0.125), 0.5));
        Check("never past the end", Near(CloudClockMath.SleepCurve(0.9, 5, 0.125), 1));
        Check("first report: the nominal pace", Near(CloudClockMath.SleepRate(-1, 0.1, 0, 8), 0.125));
        Check("the reports' own pace (a 9 s sleep)", Near(CloudClockMath.SleepRate(0.2, 0.3, 0.9, 8), 0.1 / 0.9));
        Check("a burst of reports doesn't race it", Near(CloudClockMath.SleepRate(0.2, 0.9, 0.2, 8), 0.5));
        Check("a stalled sleeper doesn't stop it", Near(CloudClockMath.SleepRate(0.2, 0.2001, 2, 8), 0.125 * 0.25));
        Check("reports far apart: the nominal pace", Near(CloudClockMath.SleepRate(0.2, 0.5, 6, 8), 0.125));

        Console.WriteLine(_failed == 0 ? "ALL PASS" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
