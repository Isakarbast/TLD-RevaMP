using System;
using TLDRevamp.Net;

static class PlaceDigestTest
{
    static int _failed;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) _failed++; }
    static double Apart(double a, double b) => PlaceDigest.Apart(PlaceDigest.Of(a), 0, 0, PlaceDigest.Of(b), 0, 0);

    static int Main()
    {
        Check(Math.Abs(Apart(100.0, 100.0)) < 1e-9, "the same place");
        Check(Math.Abs(Apart(100.0, 160.0) - 60.0) < 0.11, "60 m apart");
        Check(Math.Abs(Apart(160.0, 100.0) - 60.0) < 0.11, "either way round");
        Check(Math.Abs(Apart(-3.0, 3.0) - 6.0) < 0.11, "across zero");
        Check(Math.Abs(Apart(5000000.0, 5000174.0) - 174.0) < 0.11, "at 5000 km");
        Check(Math.Abs(Apart(6553.5, 6553.7) - 0.2) < 0.11, "across the wrap: not 6.5 km");
        Check(Apart(0.0, 0.04) < 0.11, "under the resolution");
        double d = PlaceDigest.Apart(PlaceDigest.Of(1), PlaceDigest.Of(2), PlaceDigest.Of(3), PlaceDigest.Of(4), PlaceDigest.Of(6), PlaceDigest.Of(3));
        Check(Math.Abs(d - 5.0) < 0.01, "three axes");
        Console.WriteLine(_failed == 0 ? "PlaceDigestTest: all passed" : $"PlaceDigestTest: {_failed} FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
